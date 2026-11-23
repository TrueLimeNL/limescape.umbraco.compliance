namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// One deletion-log event. Each trash, restore, delete or purge is its own row, so the history of an item is the
/// sequence of its rows. The acting person, channel, timestamp and correlation id are added by the writer.
/// </summary>
/// <param name="Event">What happened; see <see cref="DeletionLogEvents"/>.</param>
/// <param name="EntityType">document or media.</param>
/// <param name="EntityKey">Key of the item.</param>
public sealed record DeletionLogRecord(string Event, string EntityType, Guid EntityKey)
{
    /// <summary>Name of the item at the time of the event (max 300 characters).</summary>
    public string? TitleSnapshot { get; init; }

    /// <summary>Why the item was purged, for example restorationPeriodEnded or manual.</summary>
    public string? Reason { get; init; }

    /// <summary>Retention class(es) that applied at the time of the event.</summary>
    public string? RetentionClass { get; init; }
}

/// <summary>
/// Deletion-log events.
/// </summary>
public static class DeletionLogEvents
{
    /// <summary>Moved to the recycle bin.</summary>
    public const string Trashed = "trashed";

    /// <summary>Restored from the recycle bin.</summary>
    public const string Restored = "restored";

    /// <summary>Deleted permanently by a person.</summary>
    public const string Deleted = "deleted";

    /// <summary>Removed from the recycle bin by the purge job.</summary>
    public const string Purged = "purged";

    /// <summary>An element of a purged document was kept (shared, retention or legal hold); the reason says why.</summary>
    public const string PurgeSkipped = "purgeSkipped";
}

/// <summary>
/// Entity types used in the logs for Umbraco items.
/// </summary>
public static class AuditEntityTypes
{
    public const string Document = "document";
    public const string Media = "media";
}
