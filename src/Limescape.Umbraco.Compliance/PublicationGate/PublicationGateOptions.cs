namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Configuration of the publication gate, bound from <c>Compliance:PublicationGate</c>. Without gated document types the
/// gate lets every publish through; without reviewer groups nobody can record an approval.
/// </summary>
public sealed class PublicationGateOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:PublicationGate";

    /// <summary>
    /// Aliases of the document types that publish only with an approval record for exactly the version being published
    ///. Matched case-insensitively.
    /// </summary>
    public IList<string> GatedDocumentTypes { get; } = [];

    /// <summary>
    /// Aliases of the Umbraco user groups whose members hold the reviewer permission. For a trusted integration
    /// that sends an acting-user token, its API user must be in one of these groups; the person in the token is recorded
    /// as the reviewer. Matched case-insensitively.
    /// </summary>
    public IList<string> ReviewerGroups { get; } = [];

    /// <summary>System fields excluded from review. Configuration binding appends aliases; code configuration can replace the array.</summary>
    public string[] ExcludedPropertyAliases { get; set; } = ["status", "owners", "archivedAt"];

    /// <summary>Lifecycle field; documents without it are unaffected.</summary>
    public string StatusPropertyAlias { get; set; } = "status";

    /// <summary>JSON array of owner person ids used by the four-eyes rule.</summary>
    public string OwnersPropertyAlias { get; set; } = "owners";

    /// <summary>Reject an owner's approval of their own material; rejection remains allowed.</summary>
    public bool RequireDifferentReviewer { get; set; } = true;

    /// <summary>True when documents of this type need an approval record to be published.</summary>
    public bool IsGated(string? documentTypeAlias) =>
        documentTypeAlias is not null && GatedDocumentTypes.Any(t => string.Equals(t, documentTypeAlias, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when one of the given user group aliases is a reviewer group.</summary>
    public bool IsReviewerGroup(IEnumerable<string> userGroupAliases) =>
        userGroupAliases.Any(g => ReviewerGroups.Contains(g, StringComparer.OrdinalIgnoreCase));
}
