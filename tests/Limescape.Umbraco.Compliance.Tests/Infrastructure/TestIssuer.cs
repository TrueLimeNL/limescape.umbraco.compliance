using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// A trusted integration for tests: an EC P-256 key pair generated per test run, and a helper that signs
/// acting-user tokens the way a server-side integration would.
/// </summary>
public sealed class TestIssuer : IDisposable
{
    public const string DefaultIssuer = "https://integration.example.test";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestIssuer(string issuer = DefaultIssuer) => Issuer = issuer;

    /// <summary>The trusted issuer of the shared test site.</summary>
    public static TestIssuer Shared { get; } = new();

    public string Issuer { get; }

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>
    /// Signs a token. Defaults: issued now, valid 30 seconds, channel app. Pass <paramref name="omit"/> to leave claims out.
    /// </summary>
    public string CreateToken(
        string subject = "person-1",
        string? name = "Test Person",
        string channel = "app",
        string? correlationId = null,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        string? issuer = null,
        string? audience = null,
        params string[] omit)
    {
        var iat = issuedAt ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = subject,
            ["channel"] = channel,
            [JwtRegisteredClaimNames.Iat] = iat.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = (iat + (lifetime ?? TimeSpan.FromSeconds(30))).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Iss] = issuer ?? Issuer,
        };
        if (name is not null)
        {
            claims[JwtRegisteredClaimNames.Name] = name;
        }

        if (correlationId is not null)
        {
            claims["cid"] = correlationId;
        }

        if (audience is not null)
        {
            claims[JwtRegisteredClaimNames.Aud] = audience;
        }

        foreach (var claim in omit)
        {
            claims.Remove(claim);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key), SecurityAlgorithms.EcdsaSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    public void Dispose() => _key.Dispose();
}
