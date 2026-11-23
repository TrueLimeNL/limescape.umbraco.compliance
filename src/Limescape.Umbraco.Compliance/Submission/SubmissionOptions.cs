namespace Limescape.Umbraco.Compliance.Submission;

/// <summary>
/// Submission guard settings, bound from <c>Compliance:Submission</c>.
/// </summary>
public sealed class SubmissionOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:Submission";

    /// <summary>
    /// Alias of the property that holds the workflow status of an item (text or dropdown). When set, every save that
    /// moves an item into one of <see cref="SubmittedValues"/> is refused while required fields are empty, whatever the
    /// route, and the submission endpoint sets this property. When empty, the guard only validates (endpoint) and does not
    /// block saves.
    /// </summary>
    public string? StatusProperty { get; set; }

    /// <summary>Status values that mean "submitted for review". Default <c>submitted</c>.</summary>
    public IList<string> SubmittedValues { get; } = [];

    /// <summary>The status value the submission endpoint sets: the first of <see cref="SubmittedValues"/>, or <c>submitted</c>.</summary>
    public string SubmitValue => SubmittedValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "submitted";

    /// <summary>All values that count as submitted (<see cref="SubmittedValues"/>, or <c>submitted</c> when none are configured).</summary>
    public IReadOnlyCollection<string> EffectiveSubmittedValues => SubmittedValues.Count > 0 ? [.. SubmittedValues] : ["submitted"];

    /// <summary>
    /// Required property aliases per document type alias, read by <see cref="ConfiguredRequiredFieldProvider"/>.
    /// The key <c>*</c> applies to every document type.
    /// </summary>
    public IDictionary<string, IList<string>> RequiredFields { get; } = new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Path of a metadata schema snapshot (JSON, fields with levels), read by <see cref="SchemaSnapshotRequiredFieldProvider"/>.
    /// Relative paths are resolved against the content root. Empty: no snapshot.
    /// </summary>
    public string? SchemaSnapshotPath { get; set; }
}
