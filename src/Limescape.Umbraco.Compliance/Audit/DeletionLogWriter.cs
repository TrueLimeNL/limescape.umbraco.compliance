using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Appends events to the deletion log. There is deliberately no update or delete: the log is append-only.
/// </summary>
public interface IDeletionLogWriter
{
    /// <summary>Appends an event attributed to <see cref="IActingUserAccessor.Current"/>. Joins the ambient scope if there is one.</summary>
    void Write(DeletionLogRecord record);

    /// <inheritdoc cref="Write"/>
    Task WriteAsync(DeletionLogRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDeletionLogWriter"/>: inserts into <c>lsDeletionLog</c> inside an Umbraco scope.
/// </summary>
public sealed class DeletionLogWriter(IScopeProvider scopeProvider, IActingUserAccessor actingUserAccessor, TimeProvider timeProvider)
    : IDeletionLogWriter
{
    /// <inheritdoc />
    public void Write(DeletionLogRecord record)
    {
        var dto = CreateDto(record);
        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(dto);
        scope.Complete();
    }

    /// <inheritdoc />
    public async Task WriteAsync(DeletionLogRecord record, CancellationToken cancellationToken = default)
    {
        var dto = CreateDto(record);
        using var scope = scopeProvider.CreateScope();
        await scope.Database.InsertAsync(dto, cancellationToken).ConfigureAwait(false);
        scope.Complete();
    }

    private DeletionLogEntryDto CreateDto(DeletionLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Event);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EntityType);

        var actor = actingUserAccessor.Current;
        return new DeletionLogEntryDto
        {
            TimestampUtc = timeProvider.GetUtcNow().UtcDateTime,
            Event = record.Event,
            EntityType = record.EntityType,
            EntityKey = record.EntityKey,
            TitleSnapshot = AuditValues.Truncate(record.TitleSnapshot, 300),
            ActorId = actor.PersonId,
            ActorName = actor.Name,
            Channel = actor.Channel,
            Issuer = actor.Issuer,
            TechnicalUserKey = actor.TechnicalUserKey,
            Reason = AuditValues.Truncate(record.Reason, 255),
            RetentionClass = AuditValues.Truncate(record.RetentionClass, 255),
            CorrelationId = actor.CorrelationId,
        };
    }
}
