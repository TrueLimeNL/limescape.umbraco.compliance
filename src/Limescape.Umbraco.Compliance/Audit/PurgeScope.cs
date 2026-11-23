namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Marks the deletes inside it as a purge, so <see cref="ContentAuditHandler"/> writes the deletion-log event
/// <see cref="DeletionLogEvents.Purged"/> with the reason instead of <see cref="DeletionLogEvents.Deleted"/>.
/// Flows with the async context; Umbraco raises the deleted notifications when the service call's own scope ends,
/// which is inside the purge scope as long as the caller has no ambient scope open.
/// </summary>
public sealed class PurgeScope : IDisposable
{
    private static readonly AsyncLocal<PurgeScope?> Ambient = new();

    private readonly PurgeScope? _parent;

    private PurgeScope(string reason)
    {
        Reason = reason;
        _parent = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>Why the items are purged, for example <see cref="PurgeReasons.RestorationPeriodEnded"/>.</summary>
    public string Reason { get; }

    /// <summary>The purge in progress, or null.</summary>
    public static PurgeScope? Current => Ambient.Value;

    /// <summary>Starts a purge scope; dispose it when the deletes are done.</summary>
    public static PurgeScope Begin(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PurgeScope(reason);
    }

    /// <inheritdoc />
    public void Dispose() => Ambient.Value = _parent;
}

/// <summary>
/// Reasons recorded with a <see cref="DeletionLogEvents.Purged"/> event.
/// </summary>
public static class PurgeReasons
{
    /// <summary>The item stayed in the recycle bin longer than the restoration period.</summary>
    public const string RestorationPeriodEnded = "restorationPeriodEnded";

    /// <summary>The media item is an element of a document that was purged; the reason is <c>elementOfPurgedMaterial:{document key}</c>.</summary>
    public const string ElementOfPurgedMaterial = "elementOfPurgedMaterial";

    /// <summary>The reason recorded for an element of the purged document <paramref name="materialKey"/>: <c>{code}:{key}</c>.</summary>
    public static string ElementOf(Guid materialKey, string code = ElementOfPurgedMaterial) => $"{code}:{materialKey}";
}
