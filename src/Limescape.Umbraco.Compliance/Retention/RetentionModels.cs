namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// The event from which a retention period runs.
/// </summary>
public enum RetentionStartEvent
{
    /// <summary>The archive date: the date property configured in <see cref="RetentionOptions.ArchivedDatePropertyAlias"/>.</summary>
    Archived,

    /// <summary>The latest publication date. Media have no publication; for media this is the creation date.</summary>
    Published,

    /// <summary>The latest modification (save) date.</summary>
    LastModified,

    /// <summary>A date property of the item, named by <see cref="RetentionClass.StartPropertyAlias"/>.</summary>
    DateProperty,
}

/// <summary>
/// What happens when a retention period has ended. The package never deletes anything itself.
/// </summary>
public enum RetentionEndAction
{
    /// <summary>The item may be deleted from then on; it is listed as ended.</summary>
    AllowDeletion,

    /// <summary>The item is listed for review and a <see cref="RetentionEndedNotification"/> is raised.</summary>
    Notify,
}

/// <summary>
/// What a retention class is assigned to.
/// </summary>
public enum RetentionAssignmentTarget
{
    /// <summary>Every item of a document or media type.</summary>
    ContentType,

    /// <summary>Every item whose property has a given value (a category), optionally only of one type.</summary>
    PropertyValue,

    /// <summary>One item. Item assignments override type and property-value assignments for that item.</summary>
    Item,
}

/// <summary>
/// A retention period expressed in calendar units. Applied in the order years, months, days; a month end is
/// clamped (31 January plus one month is the last day of February).
/// </summary>
public readonly record struct RetentionDuration(int Years, int Months, int Days)
{
    /// <summary>True when the duration adds nothing.</summary>
    public bool IsZero => Years == 0 && Months == 0 && Days == 0;

    /// <summary>The end of a period that starts at <paramref name="start"/>.</summary>
    public DateTime AddTo(DateTime start) => start.AddYears(Years).AddMonths(Months).AddDays(Days);

    /// <summary>Approximate length in days, only for comparing durations in messages and sorting.</summary>
    public int ApproximateDays => (Years * 365) + (Months * 30) + Days;

    /// <inheritdoc />
    public override string ToString() => $"P{Years}Y{Months}M{Days}D";
}

/// <summary>
/// A retention class.
/// </summary>
/// <param name="Key">Stable key.</param>
/// <param name="Name">Name shown to administrators and in the content app.</param>
/// <param name="Duration">Length of the retention period.</param>
/// <param name="StartEvent">Event the period runs from.</param>
/// <param name="EndAction">What happens when the period has ended.</param>
public sealed record RetentionClass(Guid Key, string Name, RetentionDuration Duration, RetentionStartEvent StartEvent, RetentionEndAction EndAction)
{
    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Property alias of the start date when <see cref="StartEvent"/> is <see cref="RetentionStartEvent.DateProperty"/>.</summary>
    public string? StartPropertyAlias { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>Last change time (UTC).</summary>
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>
/// Assignment of a retention class.
/// </summary>
/// <param name="Id">Database id.</param>
/// <param name="ClassKey">Key of the assigned retention class.</param>
/// <param name="Target">What the class is assigned to.</param>
public sealed record RetentionAssignment(int Id, Guid ClassKey, RetentionAssignmentTarget Target)
{
    /// <summary>Document or media type key; required for <see cref="RetentionAssignmentTarget.ContentType"/>, optional for <see cref="RetentionAssignmentTarget.PropertyValue"/>.</summary>
    public Guid? ContentTypeKey { get; init; }

    /// <summary>Property alias for <see cref="RetentionAssignmentTarget.PropertyValue"/>.</summary>
    public string? PropertyAlias { get; init; }

    /// <summary>Property value for <see cref="RetentionAssignmentTarget.PropertyValue"/>; compared case-insensitively.</summary>
    public string? PropertyValue { get; init; }

    /// <summary>Document or media key for <see cref="RetentionAssignmentTarget.Item"/>.</summary>
    public Guid? ItemKey { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// The state of an item's retention.
/// </summary>
public enum RetentionStatus
{
    /// <summary>No retention class applies.</summary>
    None,

    /// <summary>A class applies but its start event has not happened yet (for example: not archived yet). The period is open-ended.</summary>
    Pending,

    /// <summary>The retention period runs until a date in the future.</summary>
    Active,

    /// <summary>The retention period has ended.</summary>
    Ended,
}
