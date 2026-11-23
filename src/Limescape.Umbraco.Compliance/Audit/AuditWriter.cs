using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Appends entries to the audit log. There is deliberately no update or delete: the log is append-only.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Appends an entry attributed to <see cref="IActingUserAccessor.Current"/>. Joins the ambient scope if there is one.</summary>
    void Write(AuditRecord record);

    /// <inheritdoc cref="Write"/>
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IAuditWriter"/>: inserts into <c>lsAudit</c> inside an Umbraco scope, so an entry written from a
/// notification handler is committed or rolled back together with the operation.
/// </summary>
public sealed class AuditWriter(IScopeProvider scopeProvider, IActingUserAccessor actingUserAccessor, TimeProvider timeProvider)
    : IAuditWriter
{
    /// <inheritdoc />
    public void Write(AuditRecord record)
    {
        var dto = CreateDto(record);
        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(dto);
        scope.Complete();
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        var dto = CreateDto(record);
        using var scope = scopeProvider.CreateScope();
        await scope.Database.InsertAsync(dto, cancellationToken).ConfigureAwait(false);
        scope.Complete();
    }

    private AuditEntryDto CreateDto(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EntityType);

        var actor = actingUserAccessor.Current;
        return new AuditEntryDto
        {
            TimestampUtc = timeProvider.GetUtcNow().UtcDateTime,
            ActorId = actor.PersonId,
            ActorName = actor.Name,
            Channel = actor.Channel,
            Issuer = actor.Issuer,
            TechnicalUserKey = actor.TechnicalUserKey,
            TechnicalUserName = actor.TechnicalUserName,
            Action = record.Action,
            EntityType = record.EntityType,
            EntityKey = record.EntityKey,
            Field = AuditValues.Truncate(record.Field, 255),
            OldValue = AuditValues.Truncate(record.OldValue),
            NewValue = AuditValues.Truncate(record.NewValue),
            CorrelationId = actor.CorrelationId,
        };
    }
}
