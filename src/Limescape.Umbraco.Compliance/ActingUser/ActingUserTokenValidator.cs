using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// Validates acting-user tokens (compact JWS).
/// </summary>
public interface IActingUserTokenValidator
{
    Task<ActingUserTokenValidationResult> ValidateAsync(string token);
}

/// <summary>
/// Accepts a token only when it is signed with an asymmetric key of the trusted issuer named in its <c>iss</c> claim,
/// carries <c>sub</c>, <c>channel</c>, <c>iat</c> and <c>exp</c>, lives at most <see cref="ActingUserOptions.MaxLifetime"/>,
/// and is valid now (within <see cref="ActingUserOptions.ClockSkew"/>). Signature, issuer, audience and lifetime are
/// checked by <see cref="TrustedIssuerTokenValidator"/>, shared with the Agent API's delegated person tokens.
/// </summary>
public sealed partial class ActingUserTokenValidator(IOptionsMonitor<ActingUserOptions> options, TimeProvider timeProvider)
    : IActingUserTokenValidator
{
    /// <summary>Upper bound for the header value; longer values are rejected before parsing.</summary>
    public const int MaxTokenLength = TrustedIssuerTokenValidator.MaxTokenLength;

    private readonly TrustedIssuerTokenValidator _tokens = new(ActingUserOptions.MaxLifetime);

    /// <inheritdoc />
    public async Task<ActingUserTokenValidationResult> ValidateAsync(string token)
    {
        var current = options.CurrentValue;
        var result = await _tokens.ValidateAsync(token, current.TrustedIssuers, current.ClockSkew, timeProvider.GetUtcNow()).ConfigureAwait(false);
        return result.IsValid
            ? ValidateClaims(result.Token!, result.Issuer, result.IssuedAt, result.ExpiresAt)
            : ActingUserTokenValidationResult.Failure(result.Error!);
    }

    /// <summary>Shape of a channel claim: lowercase, starts with a letter, at most 32 characters.</summary>
    public static bool IsValidChannel(string? channel) => channel is not null && ChannelPattern().IsMatch(channel);

    private static ActingUserTokenValidationResult ValidateClaims(JsonWebToken jwt, string issuer, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var subject = StringClaim(jwt, JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
        {
            return ActingUserTokenValidationResult.Failure("sub is missing or too long");
        }

        var channel = StringClaim(jwt, "channel");
        if (!IsValidChannel(channel))
        {
            return ActingUserTokenValidationResult.Failure("channel is missing or invalid");
        }

        var name = StringClaim(jwt, JwtRegisteredClaimNames.Name);
        if (name is { Length: > 255 })
        {
            return ActingUserTokenValidationResult.Failure("name is too long");
        }

        var correlationId = StringClaim(jwt, "cid");
        if (correlationId is { Length: > 128 })
        {
            return ActingUserTokenValidationResult.Failure("cid is too long");
        }

        return ActingUserTokenValidationResult.Success(
            new ActingUserToken(subject, name, channel!, correlationId, issuer, issuedAt, expiresAt));
    }

    private static string? StringClaim(JsonWebToken jwt, string name) =>
        jwt.TryGetPayloadValue<string>(name, out var value) ? value : null;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}
