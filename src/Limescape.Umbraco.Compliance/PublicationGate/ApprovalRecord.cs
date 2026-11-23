namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// One recorded decision of a reviewer about one version of a document.
/// </summary>
/// <param name="ContentKey">Key of the document.</param>
/// <param name="VersionId">Umbraco version id of the draft that was reviewed.</param>
/// <param name="VersionHash">Fingerprint of the draft that was reviewed (<see cref="IContentVersionHasher"/>).</param>
/// <param name="Decision"><see cref="ApprovalDecisions.Approved"/> or <see cref="ApprovalDecisions.Rejected"/>.</param>
public sealed record ApprovalRecord(Guid ContentKey, int VersionId, string VersionHash, string Decision)
{
    /// <summary>Row id; 0 before it is stored.</summary>
    public long Id { get; init; }

    /// <summary>Person id of the reviewer: from the acting-user token, or the Umbraco user key.</summary>
    public string ReviewerId { get; init; } = string.Empty;

    /// <summary>Display name of the reviewer.</summary>
    public string? ReviewerName { get; init; }

    /// <summary>Channel the reviewer acted through.</summary>
    public string? Channel { get; init; }

    /// <summary>Where the decision was recorded; see <see cref="ApprovalSources"/>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Short comment, at most 2,000 characters.</summary>
    public string? Comment { get; init; }

    /// <summary>Findings of the quality check, free text.</summary>
    public string? Findings { get; init; }

    /// <summary>Moment of the decision (UTC).</summary>
    public DateTime DecidedUtc { get; init; }

    /// <summary>The authenticated Umbraco user that carried out the request.</summary>
    public Guid? TechnicalUserKey { get; init; }

    /// <summary>Correlation id of the request.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Decision values.
/// </summary>
public static class ApprovalDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    /// <summary>True for a known decision value (case-sensitive, lowercase).</summary>
    public static bool IsValid(string? decision) => decision is Approved or Rejected;
}

/// <summary>
/// Where an approval record came from.
/// </summary>
public static class ApprovalSources
{
    /// <summary>A person signed in to the backoffice called the approval API.</summary>
    public const string Backoffice = "backoffice";

    /// <summary>A trusted integration called the approval API with an acting-user token for the reviewer.</summary>
    public const string Integration = "integration";

    /// <summary>Written when an Umbraco Workflow approval flow completed.</summary>
    public const string Workflow = "workflow";
}
