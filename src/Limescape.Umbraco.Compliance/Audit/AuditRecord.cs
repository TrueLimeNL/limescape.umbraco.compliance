namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// One audited change. The acting person, channel, timestamp and correlation id are added by the writer.
/// </summary>
/// <param name="Action">What happened; see <see cref="AuditActions"/>.</param>
/// <param name="EntityType">Kind of entity, for example document, media, documentType or retentionClass.</param>
/// <param name="EntityKey">Key of the entity, if it has one.</param>
public sealed record AuditRecord(string Action, string EntityType, Guid? EntityKey)
{
    /// <summary>Changed field (property alias) for field-level entries.</summary>
    public string? Field { get; init; }

    /// <summary>Value before the change; truncated to <see cref="AuditValues.MaxLength"/> characters.</summary>
    public string? OldValue { get; init; }

    /// <summary>Value after the change; truncated to <see cref="AuditValues.MaxLength"/> characters.</summary>
    public string? NewValue { get; init; }
}

/// <summary>
/// Audited actions.
/// </summary>
public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Publish = "publish";
    public const string Unpublish = "unpublish";
    public const string Move = "move";
    public const string Trash = "trash";
    public const string Restore = "restore";
    public const string Delete = "delete";
    /// <summary>A typed link between two documents was created; written on both documents.</summary>
    public const string Relate = "relate";
    /// <summary>A typed link between two documents was removed; written on both documents.</summary>
    public const string Unrelate = "unrelate";
    public const string PermissionChange = "permissionChange";
    public const string Approval = "approval";
    public const string RetentionChange = "retentionChange";
    /// <summary>A metadata field was archived or restored, or the deprecated codes of a field changed; the field holds the property alias.</summary>
    public const string FieldStateChange = "fieldStateChange";
    /// <summary>A person confirmed the archive of a document (status archived, archive date, unpublished); the field holds the reason.</summary>
    public const string Archive = "archive";

    /// <summary>A trash or delete was refused by the deletion block; the field names the operation.</summary>
    public const string DeleteBlocked = "deleteBlocked";
    /// <summary>The publication gate cancelled a publish because the version has no approval.</summary>
    public const string PublishBlocked = "publishBlocked";
}

/// <summary>
/// Limits for stored values.
/// </summary>
public static class AuditValues
{
    /// <summary>Maximum stored length of an old or new value.</summary>
    public const int MaxLength = 2000;

    /// <summary>Marker that ends a truncated value.</summary>
    public const string TruncationMarker = "…";

    /// <summary>Cuts a value to <see cref="MaxLength"/> characters, ending in <see cref="TruncationMarker"/> when cut.</summary>
    public static string? Truncate(string? value, int maxLength = MaxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength - TruncationMarker.Length;
        // Do not split a surrogate pair.
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), TruncationMarker);
    }
}
