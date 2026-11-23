namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// Configuration for acting-user tokens sent by trusted server-side integrations, bound from
/// <c>Compliance:ActingUser</c>. Without trusted issuers every request that carries the header is rejected.
/// </summary>
public sealed class ActingUserOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:ActingUser";

    /// <summary>Default request header that carries the token.</summary>
    public const string DefaultHeaderName = "X-Limescape-Acting-User";

    /// <summary>Maximum lifetime of a token (<c>exp - iat</c>). Not configurable.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Request header that carries the token.</summary>
    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>Integrations whose tokens are accepted.</summary>
    public IList<TrustedIssuerOptions> TrustedIssuers { get; } = [];

    /// <summary>Allowed clock difference between the issuer and this site. Default 10 seconds.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// One trusted integration: the <c>iss</c> value it puts in its tokens and the public keys that verify them.
/// </summary>
public sealed class TrustedIssuerOptions
{
    /// <summary>Exact value of the <c>iss</c> claim.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Public keys in PEM format (<c>-----BEGIN PUBLIC KEY-----</c>), EC (P-256/384/521) or RSA (2048 bits or more).
    /// More than one key allows rotation.
    /// </summary>
    public IList<string> PublicKeys { get; } = [];

    /// <summary>When set, the token's <c>aud</c> claim must contain this value.</summary>
    public string? Audience { get; set; }
}
