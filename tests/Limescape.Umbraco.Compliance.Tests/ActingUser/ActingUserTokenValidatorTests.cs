using System.Security.Cryptography;
using System.Text;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.Compliance.Tests.ActingUser;

public sealed class ActingUserTokenValidatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TestIssuer _issuer = new("https://trusted.example.test");
    private readonly TestIssuer _forger = new("https://trusted.example.test");
    private readonly ActingUserOptions _options = new();
    private readonly ActingUserTokenValidator _validator;

    public ActingUserTokenValidatorTests()
    {
        var trusted = new TrustedIssuerOptions { Issuer = _issuer.Issuer };
        trusted.PublicKeys.Add(_issuer.PublicKeyPem);
        _options.TrustedIssuers.Add(trusted);
        _validator = new ActingUserTokenValidator(new StaticOptionsMonitor<ActingUserOptions>(_options), new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task Valid_token_yields_the_person_channel_and_correlation_id()
    {
        var token = _issuer.CreateToken(subject: "p-42", name: "Pat Example", channel: "assistant", correlationId: "c-1", issuedAt: Now);

        var result = await _validator.ValidateAsync(token);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("p-42", result.Token!.Subject);
        Assert.Equal("Pat Example", result.Token.Name);
        Assert.Equal("assistant", result.Token.Channel);
        Assert.Equal("c-1", result.Token.CorrelationId);
        Assert.Equal(_issuer.Issuer, result.Token.Issuer);
    }

    [Fact]
    public async Task Valid_token_signed_with_an_rsa_key_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        _options.TrustedIssuers[0].PublicKeys.Add(rsa.ExportSubjectPublicKeyInfoPem());
        var token = Sign(rsa, SecurityAlgorithms.RsaSha256, Claims(Now));

        var result = await _validator.ValidateAsync(token);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var token = _issuer.CreateToken(issuedAt: Now.AddSeconds(-90), lifetime: TimeSpan.FromSeconds(60));

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_within_clock_skew_after_expiry_is_still_accepted()
    {
        var token = _issuer.CreateToken(issuedAt: Now.AddSeconds(-35), lifetime: TimeSpan.FromSeconds(30));

        var result = await _validator.ValidateAsync(token);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public async Task Token_with_a_lifetime_over_60_seconds_is_rejected()
    {
        var token = _issuer.CreateToken(issuedAt: Now, lifetime: TimeSpan.FromSeconds(61));

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Token_issued_in_the_future_is_rejected()
    {
        var token = _issuer.CreateToken(issuedAt: Now.AddMinutes(1));

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Forged_token_signed_with_another_key_is_rejected()
    {
        var token = _forger.CreateToken(issuedAt: Now);

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var parts = _issuer.CreateToken(subject: "p-1", issuedAt: Now).Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("p-1", "p-2", StringComparison.Ordinal);
        var tampered = string.Join('.', parts[0], Base64UrlEncoder.Encode(payload), parts[2]);

        var result = await _validator.ValidateAsync(tampered);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Token_from_an_untrusted_issuer_is_rejected()
    {
        var token = _issuer.CreateToken(issuedAt: Now, issuer: "https://other.example.test");

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
        Assert.Contains("not trusted", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsigned_token_is_rejected()
    {
        var unsigned = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }
            .CreateToken(new SecurityTokenDescriptor { Claims = Claims(Now) });

        var result = await _validator.ValidateAsync(unsigned);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Token_signed_with_hmac_over_the_public_key_is_rejected()
    {
        // Classic algorithm-confusion attack: use the published public key as an HMAC secret.
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_issuer.PublicKeyPem));
        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = Claims(Now),
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256),
        });

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(JwtRegisteredClaimNames.Sub)]
    [InlineData("channel")]
    [InlineData(JwtRegisteredClaimNames.Iat)]
    [InlineData(JwtRegisteredClaimNames.Exp)]
    public async Task Token_without_a_required_claim_is_rejected(string claim)
    {
        var token = _issuer.CreateToken(issuedAt: Now, omit: claim);

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("Backoffice")]
    [InlineData("app; drop")]
    [InlineData("")]
    public async Task Token_with_an_invalid_channel_is_rejected(string channel)
    {
        var token = _issuer.CreateToken(issuedAt: Now, channel: channel);

        var result = await _validator.ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Audience_is_checked_when_configured()
    {
        _options.TrustedIssuers[0].Audience = "https://content.example.test";

        var wrong = await _validator.ValidateAsync(_issuer.CreateToken(issuedAt: Now, audience: "https://elsewhere.example.test"));
        var right = await _validator.ValidateAsync(_issuer.CreateToken(issuedAt: Now, audience: "https://content.example.test"));

        Assert.False(wrong.IsValid);
        Assert.True(right.IsValid, right.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task Malformed_value_is_rejected(string value)
    {
        var result = await _validator.ValidateAsync(value);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Token_is_rejected_when_no_issuer_is_configured()
    {
        var validator = new ActingUserTokenValidator(new StaticOptionsMonitor<ActingUserOptions>(new ActingUserOptions()), new FixedTimeProvider(Now));

        var result = await validator.ValidateAsync(_issuer.CreateToken(issuedAt: Now));

        Assert.False(result.IsValid);
    }

    public void Dispose()
    {
        _issuer.Dispose();
        _forger.Dispose();
    }

    private Dictionary<string, object> Claims(DateTimeOffset issuedAt) => new()
    {
        [JwtRegisteredClaimNames.Sub] = "p-1",
        ["channel"] = "app",
        [JwtRegisteredClaimNames.Iss] = _issuer.Issuer,
        [JwtRegisteredClaimNames.Iat] = issuedAt.ToUnixTimeSeconds(),
        [JwtRegisteredClaimNames.Exp] = issuedAt.AddSeconds(30).ToUnixTimeSeconds(),
    };

    private static string Sign(RSA rsa, string algorithm, Dictionary<string, object> claims) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), algorithm),
        });
}
