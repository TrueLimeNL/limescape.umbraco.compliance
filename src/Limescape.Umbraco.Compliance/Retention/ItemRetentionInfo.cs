namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Retention of one item as exposed by the API and shown in the content app.
/// </summary>
/// <param name="ItemKey">Document or media key.</param>
/// <param name="EntityType">document or media.</param>
/// <param name="Status">Status now.</param>
/// <param name="Rules">Every applicable class with its rule, longest first.</param>
public sealed record ItemRetentionInfo(Guid ItemKey, string EntityType, RetentionStatus Status, IReadOnlyList<ItemRetentionRuleInfo> Rules)
{
    /// <summary>End of the effective period (UTC); null when open-ended or when no class applies.</summary>
    public DateTime? RetentionUntil { get; init; }

    /// <summary>Start of the effective period (UTC), for example the archive date; null while it has not started.</summary>
    public DateTime? StartDate { get; init; }

    /// <summary>True when a class applies whose start event has not happened yet.</summary>
    public bool OpenEnded { get; init; }

    /// <summary>Key of the class that governs the effective period.</summary>
    public Guid? GoverningClassKey { get; init; }

    /// <summary>End action of the governing class.</summary>
    public RetentionEndAction? EndAction { get; init; }

    /// <summary>True when a later calculation gave a shorter period that was not confirmed by an administrator, so the earlier period is kept.</summary>
    public bool HeldFromEarlier { get; init; }

    /// <summary>Legal hold (set by administrators; blocks deletion regardless of retention).</summary>
    public bool LegalHold { get; init; }

    /// <summary>When the stored period was calculated; null when the result was calculated for this request only.</summary>
    public DateTime? CalculatedUtc { get; init; }

    /// <summary>When the daily job reported the end of the period.</summary>
    public DateTime? EndReportedUtc { get; init; }
}

/// <summary>
/// One applicable class and how it applies to the item.
/// </summary>
/// <param name="ClassKey">Class key.</param>
/// <param name="ClassName">Class name at calculation time.</param>
/// <param name="StartEvent">Start event of the class.</param>
/// <param name="Duration">Duration of the class.</param>
/// <param name="EndAction">End action of the class.</param>
/// <param name="Source">The assignment target that made the class apply.</param>
public sealed record ItemRetentionRuleInfo(
    Guid ClassKey,
    string ClassName,
    RetentionStartEvent StartEvent,
    RetentionDuration Duration,
    RetentionEndAction EndAction,
    RetentionAssignmentTarget Source)
{
    /// <summary>Property alias of the start date, for <see cref="RetentionStartEvent.DateProperty"/> and <see cref="RetentionStartEvent.Archived"/>.</summary>
    public string? StartPropertyAlias { get; init; }

    /// <summary>Start of the period; null while the start event has not happened.</summary>
    public DateTime? StartDate { get; init; }

    /// <summary>End of the period; null while the start event has not happened.</summary>
    public DateTime? Until { get; init; }

    /// <summary>True for the rule that governs the effective period.</summary>
    public bool Governing { get; init; }
}

/// <summary>
/// Outcome of recalculating items.
/// </summary>
/// <param name="Recalculated">Items recalculated.</param>
/// <param name="Changed">Items whose effective period changed.</param>
/// <param name="ShorteningRefused">Items that kept a longer period because shortening was not confirmed.</param>
public sealed record RetentionRecalculation(int Recalculated, int Changed, IReadOnlyList<Guid> ShorteningRefused)
{
    /// <summary>Nothing recalculated.</summary>
    public static RetentionRecalculation Empty { get; } = new(0, 0, []);

    /// <summary>Adds two outcomes.</summary>
    public RetentionRecalculation Add(RetentionRecalculation other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Recalculated + other.Recalculated, Changed + other.Changed, [.. ShorteningRefused, .. other.ShorteningRefused]);
    }
}

/// <summary>
/// Input for creating or updating a retention class.
/// </summary>
public sealed record RetentionClassInput(string Name, RetentionDuration Duration, RetentionStartEvent StartEvent, RetentionEndAction EndAction)
{
    /// <summary>Optional description (max 2,000 characters).</summary>
    public string? Description { get; init; }

    /// <summary>Required when <see cref="StartEvent"/> is <see cref="RetentionStartEvent.DateProperty"/>.</summary>
    public string? StartPropertyAlias { get; init; }
}

/// <summary>
/// Input that the package refuses, with a stable code for API clients.
/// </summary>
public sealed class RetentionValidationException(string code, string message) : Exception(message)
{
    /// <summary>Stable error code, for example <c>retention_class_in_use</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Stable error codes of the retention API.
/// </summary>
public static class RetentionErrorCodes
{
    public const string Invalid = "retention_invalid";
    public const string ClassNotFound = "retention_class_not_found";
    public const string ClassInUse = "retention_class_in_use";
    public const string ContentTypeNotFound = "retention_content_type_not_found";
    public const string ItemNotFound = "retention_item_not_found";
    public const string ShorteningRequiresAdministrator = "retention_shortening_requires_administrator";
}
