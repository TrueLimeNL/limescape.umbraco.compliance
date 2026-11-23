namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// The person a change is attributed to, as recorded in the audit and deletion logs.
/// </summary>
/// <param name="PersonId">Person id: the token's <c>sub</c>, the Umbraco user key, or <see cref="SystemId"/>.</param>
/// <param name="Name">Display name, if known.</param>
/// <param name="Channel">Channel: from the token, <see cref="ActorChannels.Backoffice"/> or <see cref="ActorChannels.Api"/> for Umbraco users, <see cref="ActorChannels.System"/> otherwise.</param>
public sealed record Actor(string PersonId, string? Name, string Channel)
{
    /// <summary>Person id used when no person can be identified (background jobs, anonymous requests).</summary>
    public const string SystemId = "system";

    /// <summary>Issuer of the acting-user token; null when the person is the Umbraco user itself.</summary>
    public string? Issuer { get; init; }

    /// <summary>Key of the authenticated Umbraco user that carried out the request (for example an API user).</summary>
    public Guid? TechnicalUserKey { get; init; }

    /// <summary>Name of the authenticated Umbraco user that carried out the request.</summary>
    public string? TechnicalUserName { get; init; }

    /// <summary>Correlation id: the token's <c>cid</c>, the <c>X-Correlation-Id</c> header, or the request trace id.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>True when the person comes from a validated acting-user token.</summary>
    public bool FromTrustedIntegration => Issuer is not null;

    /// <summary>The system itself, for work outside a request or without a signed-in user.</summary>
    public static Actor System(string? correlationId = null) =>
        new(SystemId, "System", ActorChannels.System) { CorrelationId = correlationId };
}

/// <summary>
/// Channel values the package sets itself. Tokens from trusted integrations carry their own channel
/// (lowercase, for example app, assistant, api or job).
/// </summary>
public static class ActorChannels
{
    /// <summary>A person signed in to the Umbraco backoffice.</summary>
    public const string Backoffice = "backoffice";

    /// <summary>An Umbraco API user (client credentials) without an acting-user token.</summary>
    public const string Api = "api";

    /// <summary>No person: background job or anonymous request.</summary>
    public const string System = "system";
}
