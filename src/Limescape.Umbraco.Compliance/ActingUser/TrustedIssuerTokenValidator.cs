using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// Shared validation of a compact JWS from a trusted issuer (<see cref="TrustedIssuerOptions"/>): asymmetric signature
/// (ES*, RS*, PS*; symmetric and unsigned tokens are refused), exact <c>iss</c>, audience when the issuer has one,
/// required <c>iat</c> and <c>exp</c>, a capped lifetime (<c>exp - iat</c>) and validity now within a clock skew.
/// The acting-user header uses it with a 60-second cap; the Agent API's delegated person tokens with 600 seconds.
/// Claims beyond the registered ones are left to the caller.
/// </summary>
public sealed class TrustedIssuerTokenValidator(TimeSpan maxLifetime)
{
    /// <summary>Upper bound for a token; longer values are rejected before parsing.</summary>
    public const int MaxTokenLength = 8192;

    private static readonly string[] AsymmetricAlgorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly ConcurrentDictionary<string, SecurityKey> _keys = new(StringComparer.Ordinal);

    /// <summary>The longest accepted <c>exp - iat</c>.</summary>
    public TimeSpan MaxLifetime { get; } = maxLifetime;

    /// <summary>Whether the token can be parsed and names one of <paramref name="issuers"/> in <c>iss</c>, without validating it.</summary>
    public bool NamesTrustedIssuer(string? token, IEnumerable<TrustedIssuerOptions> issuers)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength || !_handler.CanReadToken(token))
        {
            return false;
        }

        try
        {
            var issuer = _handler.ReadJsonWebToken(token).Issuer;
            return !string.IsNullOrEmpty(issuer) && issuers.Any(i => string.Equals(i.Issuer, issuer, StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Validates <paramref name="token"/> against <paramref name="issuers"/> at <paramref name="now"/>.</summary>
    public async Task<TrustedIssuerTokenResult> ValidateAsync(string? token, IEnumerable<TrustedIssuerOptions> issuers, TimeSpan clockSkew, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength || !_handler.CanReadToken(token))
        {
            return TrustedIssuerTokenResult.Failure("malformed token");
        }

        JsonWebToken unvalidated;
        try
        {
            unvalidated = _handler.ReadJsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return TrustedIssuerTokenResult.Failure("malformed token");
        }

        var issuer = issuers.FirstOrDefault(i => string.Equals(i.Issuer, unvalidated.Issuer, StringComparison.Ordinal));
        if (issuer is null || issuer.PublicKeys.Count == 0)
        {
            return TrustedIssuerTokenResult.Failure("issuer is not trusted");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer.Issuer,
            ValidateIssuer = true,
            IssuerSigningKeys = issuer.PublicKeys.Select(GetKey).ToList(),
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = AsymmetricAlgorithms,
            RequireSignedTokens = true,
            ValidateAudience = !string.IsNullOrEmpty(issuer.Audience),
            ValidAudience = issuer.Audience,
            // Lifetime is checked below against the caller's clock, with the cap.
            ValidateLifetime = false,
            RequireExpirationTime = true,
        };

        var result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return TrustedIssuerTokenResult.Failure("signature, issuer or audience is invalid");
        }

        if (!jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Iat, out var iatSeconds)
            || !jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Exp, out var expSeconds))
        {
            return TrustedIssuerTokenResult.Failure("iat and exp are required");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);

        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaxLifetime)
        {
            return TrustedIssuerTokenResult.Failure("lifetime exceeds the maximum");
        }

        if (issuedAt > now + clockSkew)
        {
            return TrustedIssuerTokenResult.Failure("token is not yet valid");
        }

        if (expiresAt + clockSkew <= now)
        {
            return TrustedIssuerTokenResult.Failure("token has expired");
        }

        if (jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Nbf, out var nbfSeconds)
            && DateTimeOffset.FromUnixTimeSeconds(nbfSeconds) > now + clockSkew)
        {
            return TrustedIssuerTokenResult.Failure("token is not yet valid");
        }

        return TrustedIssuerTokenResult.Success(jwt, issuer.Issuer, issuedAt, expiresAt);
    }

    /// <summary>Whether <paramref name="pem"/> is a usable public key (EC, or RSA of 2048 bits or more), for configuration checks at startup.</summary>
    public static bool IsPublicKeyPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            var key = CreateKey(pem);
            (key as IDisposable)?.Dispose();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private SecurityKey GetKey(string pem) => _keys.GetOrAdd(pem, CreateKey);

    private static SecurityKey CreateKey(string pem)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            return new ECDsaSecurityKey(ecdsa);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            // Not an EC key; try RSA below.
            ecdsa.Dispose();
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException("A public key of a trusted issuer is not a PEM-encoded EC or RSA public key.", ex);
        }

        if (rsa.KeySize < 2048)
        {
            rsa.Dispose();
            throw new InvalidOperationException("An RSA public key of a trusted issuer is shorter than 2048 bits.");
        }

        return new RsaSecurityKey(rsa);
    }
}

/// <summary>
/// Outcome of <see cref="TrustedIssuerTokenValidator.ValidateAsync"/>. <see cref="Error"/> is safe to log; it never contains the token.
/// </summary>
public sealed record TrustedIssuerTokenResult(JsonWebToken? Token, string Issuer, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string? Error)
{
    public bool IsValid => Token is not null;

    public static TrustedIssuerTokenResult Success(JsonWebToken token, string issuer, DateTimeOffset issuedAt, DateTimeOffset expiresAt) =>
        new(token, issuer, issuedAt, expiresAt, null);

    public static TrustedIssuerTokenResult Failure(string error) => new(null, string.Empty, default, default, error);
}
