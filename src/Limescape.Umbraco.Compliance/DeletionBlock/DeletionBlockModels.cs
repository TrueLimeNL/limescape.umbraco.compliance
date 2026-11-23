namespace Limescape.Umbraco.Compliance.DeletionBlock;

/// <summary>
/// Why an item cannot be deleted.
/// </summary>
public enum DeletionBlockReason
{
    /// <summary>A legal hold is set; it blocks regardless of retention.</summary>
    LegalHold,

    /// <summary>The retention period runs until a date in the future.</summary>
    Retention,

    /// <summary>A retention class applies whose start event has not happened yet: the period runs indefinitely for now.</summary>
    RetentionOpenEnded,
}

/// <summary>
/// The operation that was attempted, for the audit log and the messages.
/// </summary>
public enum DeletionBlockOperation
{
    /// <summary>Move to the recycle bin.</summary>
    Trash,

    /// <summary>Delete permanently (from the tree or from the recycle bin).</summary>
    Delete,

    /// <summary>Empty the recycle bin.</summary>
    EmptyRecycleBin,

    /// <summary>Delete a document or media type, which deletes its items and trashes their children.</summary>
    DeleteType,

    /// <summary>Purge from the recycle bin after the restoration period (see <c>IPurgeService</c>).</summary>
    Purge,
}

/// <summary>
/// One item that blocks a deletion.
/// </summary>
/// <param name="ItemKey">The item that is under retention or legal hold.</param>
/// <param name="EntityType">document or media.</param>
/// <param name="Name">Name of the item.</param>
/// <param name="Reason">Why it blocks.</param>
public sealed record DeletionBlock(Guid ItemKey, string EntityType, string Name, DeletionBlockReason Reason)
{
    /// <summary>End of the retention period (UTC) for <see cref="DeletionBlockReason.Retention"/>.</summary>
    public DateTime? RetentionUntil { get; init; }

    /// <summary>Name of the governing retention class, if any.</summary>
    public string? RetentionClass { get; init; }

    /// <summary>
    /// The item the operation was attempted on, when the block comes from one of its descendants (for example: trashing
    /// a folder that contains an item under retention). Null when the item itself blocks.
    /// </summary>
    public Guid? ViaItemKey { get; init; }
}

/// <summary>
/// Stable error codes of the deletion block and the legal-hold API.
/// </summary>
public static class DeletionBlockErrorCodes
{
    /// <summary>A deletion, trash or recycle-bin purge was refused because of retention or a legal hold.</summary>
    public const string RetentionBlocksDeletion = "retention_blocks_deletion";

    /// <summary>Only administrators can set or lift a legal hold.</summary>
    public const string LegalHoldRequiresAdministrator = "retention_legal_hold_requires_administrator";
}
