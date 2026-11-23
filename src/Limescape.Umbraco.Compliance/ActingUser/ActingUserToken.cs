namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// The validated content of an acting-user token.
/// </summary>
/// <param name="Subject">Person id in the issuing system (<c>sub</c>).</param>
/// <param name="Name">Display name (<c>name</c>), optional.</param>
/// <param name="Channel">Channel the person acted through (<c>channel</c>), for example app, assistant, api or job.</param>
/// <param name="CorrelationId">Correlation id (<c>cid</c>), optional.</param>
/// <param name="Issuer">The trusted issuer (<c>iss</c>).</param>
/// <param name="IssuedAt">Issue time (<c>iat</c>).</param>
/// <param name="ExpiresAt">Expiry time (<c>exp</c>).</param>
public sealed record ActingUserToken(
    string Subject,
    string? Name,
    string Channel,
    string? CorrelationId,
    string Issuer,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Result of <see cref="IActingUserTokenValidator.ValidateAsync"/>. <see cref="Error"/> is safe to log; it never contains the token.
/// </summary>
public sealed record ActingUserTokenValidationResult(ActingUserToken? Token, string? Error)
{
    public bool IsValid => Token is not null;

    public static ActingUserTokenValidationResult Success(ActingUserToken token) => new(token, null);

    public static ActingUserTokenValidationResult Failure(string error) => new(null, error);
}

/// <summary>
/// Request feature set by <see cref="ActingUserMiddleware"/> after a token has been validated.
/// </summary>
public sealed record ActingUserTokenFeature(ActingUserToken Token);
