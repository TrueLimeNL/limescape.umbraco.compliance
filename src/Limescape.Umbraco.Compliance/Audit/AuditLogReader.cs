using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Reads the audit and deletion logs with filters and paging. Newest first.
/// </summary>
public interface IAuditLogReader
{
    /// <summary>Audit entries matching <paramref name="filter"/>.</summary>
    LogPage<AuditEntry> QueryAudit(AuditLogFilter filter);

    /// <summary>Deletion-log events matching <paramref name="filter"/>; <see cref="AuditLogFilter.Action"/> filters on the event.</summary>
    LogPage<DeletionLogEntry> QueryDeletionLog(AuditLogFilter filter);

    /// <summary>When <paramref name="itemKey"/> was last moved to the recycle bin, from the deletion log; null when not logged.</summary>
    DateTime? LastTrashedUtc(Guid itemKey);
}

/// <summary>Filters for <see cref="IAuditLogReader"/>; every filter is optional and they combine with AND.</summary>
public sealed record AuditLogFilter
{
    /// <summary>Acting person id (exact).</summary>
    public string? Person { get; init; }

    /// <summary>Item (entity) key.</summary>
    public Guid? Item { get; init; }

    /// <summary>Audit action, or deletion-log event.</summary>
    public string? Action { get; init; }

    /// <summary>Entity type, for example document, media or userGroup.</summary>
    public string? EntityType { get; init; }

    /// <summary>Channel, for example backoffice, api, app or assistant.</summary>
    public string? Channel { get; init; }

    /// <summary>From this moment (UTC, inclusive).</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>Until this moment (UTC, exclusive).</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Entries to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Entries to return.</summary>
    public int Take { get; init; } = 100;
}

/// <summary>One page of log entries.</summary>
/// <param name="Total">Number of entries matching the filter.</param>
/// <param name="Skip">Entries skipped.</param>
/// <param name="Take">Requested page size.</param>
/// <param name="Items">The entries, newest first.</param>
public sealed record LogPage<T>(long Total, int Skip, int Take, IReadOnlyList<T> Items);

/// <summary>An audit entry as returned by the API.</summary>
public sealed record AuditEntry(
    long Id,
    DateTime TimestampUtc,
    string Action,
    string EntityType,
    Guid? EntityKey,
    string? Field,
    string? OldValue,
    string? NewValue,
    string ActorId,
    string? ActorName,
    string Channel,
    string? Issuer,
    Guid? TechnicalUserKey,
    string? TechnicalUserName,
    string? CorrelationId);

/// <summary>A deletion-log event as returned by the API.</summary>
public sealed record DeletionLogEntry(
    long Id,
    DateTime TimestampUtc,
    string Event,
    string EntityType,
    Guid EntityKey,
    string? TitleSnapshot,
    string? Reason,
    string? RetentionClass,
    string ActorId,
    string? ActorName,
    string Channel,
    string? Issuer,
    Guid? TechnicalUserKey,
    string? CorrelationId);

/// <summary>
/// Default <see cref="IAuditLogReader"/> on <c>lsAudit</c> and <c>lsDeletionLog</c>.
/// </summary>
public sealed class AuditLogReader(IScopeProvider scopeProvider) : IAuditLogReader
{
    /// <summary>Largest page.</summary>
    public const int MaxTake = 500;

    /// <inheritdoc />
    public LogPage<AuditEntry> QueryAudit(AuditLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql().SelectAll().From<AuditEntryDto>();
        if (filter.Person is { } person)
        {
            sql = sql.Where<AuditEntryDto>(x => x.ActorId == person);
        }

        if (filter.Item is { } item)
        {
            sql = sql.Where<AuditEntryDto>(x => x.EntityKey == item);
        }

        if (filter.Action is { } action)
        {
            sql = sql.Where<AuditEntryDto>(x => x.Action == action);
        }

        if (filter.EntityType is { } entityType)
        {
            sql = sql.Where<AuditEntryDto>(x => x.EntityType == entityType);
        }

        if (filter.Channel is { } channel)
        {
            sql = sql.Where<AuditEntryDto>(x => x.Channel == channel);
        }

        if (filter.FromUtc is { } from)
        {
            sql = sql.Where<AuditEntryDto>(x => x.TimestampUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            sql = sql.Where<AuditEntryDto>(x => x.TimestampUtc < to);
        }

        sql = sql.OrderByDescending<AuditEntryDto>(x => x.TimestampUtc).OrderByDescending<AuditEntryDto>(x => x.Id);
        var (skip, take) = Paging(filter);
        var page = scope.Database.Page<AuditEntryDto>(skip / take + 1, take, sql);
        // Page() works in whole pages; skip that is not a multiple of take is served from the next page(s).
        var items = skip % take == 0 ? page.Items : scope.Database.SkipTake<AuditEntryDto>(skip, take, sql);
        return new LogPage<AuditEntry>(page.TotalItems, skip, take, items.Select(ToEntry).ToList());
    }

    /// <inheritdoc />
    public LogPage<DeletionLogEntry> QueryDeletionLog(AuditLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql().SelectAll().From<DeletionLogEntryDto>();
        if (filter.Person is { } person)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.ActorId == person);
        }

        if (filter.Item is { } item)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.EntityKey == item);
        }

        if (filter.Action is { } action)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.Event == action);
        }

        if (filter.EntityType is { } entityType)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.EntityType == entityType);
        }

        if (filter.Channel is { } channel)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.Channel == channel);
        }

        if (filter.FromUtc is { } from)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.TimestampUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            sql = sql.Where<DeletionLogEntryDto>(x => x.TimestampUtc < to);
        }

        sql = sql.OrderByDescending<DeletionLogEntryDto>(x => x.TimestampUtc).OrderByDescending<DeletionLogEntryDto>(x => x.Id);
        var (skip, take) = Paging(filter);
        var page = scope.Database.Page<DeletionLogEntryDto>(skip / take + 1, take, sql);
        var items = skip % take == 0 ? page.Items : scope.Database.SkipTake<DeletionLogEntryDto>(skip, take, sql);
        return new LogPage<DeletionLogEntry>(page.TotalItems, skip, take, items.Select(ToEntry).ToList());
    }

    /// <inheritdoc />
    public DateTime? LastTrashedUtc(Guid itemKey)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .SelectAll()
            .From<DeletionLogEntryDto>()
            .Where<DeletionLogEntryDto>(x => x.EntityKey == itemKey && x.Event == DeletionLogEvents.Trashed)
            .OrderByDescending<DeletionLogEntryDto>(x => x.Id);
        var last = scope.Database.SkipTake<DeletionLogEntryDto>(0, 1, sql).FirstOrDefault();
        return last is null ? null : DateTime.SpecifyKind(last.TimestampUtc, DateTimeKind.Utc);
    }

    private static (int Skip, int Take) Paging(AuditLogFilter filter) => (Math.Max(0, filter.Skip), Math.Clamp(filter.Take, 1, MaxTake));

    private static AuditEntry ToEntry(AuditEntryDto d) => new(
        d.Id,
        DateTime.SpecifyKind(d.TimestampUtc, DateTimeKind.Utc),
        d.Action,
        d.EntityType,
        d.EntityKey,
        d.Field,
        d.OldValue,
        d.NewValue,
        d.ActorId,
        d.ActorName,
        d.Channel,
        d.Issuer,
        d.TechnicalUserKey,
        d.TechnicalUserName,
        d.CorrelationId);

    private static DeletionLogEntry ToEntry(DeletionLogEntryDto d) => new(
        d.Id,
        DateTime.SpecifyKind(d.TimestampUtc, DateTimeKind.Utc),
        d.Event,
        d.EntityType,
        d.EntityKey,
        d.TitleSnapshot,
        d.Reason,
        d.RetentionClass,
        d.ActorId,
        d.ActorName,
        d.Channel,
        d.Issuer,
        d.TechnicalUserKey,
        d.CorrelationId);
}
