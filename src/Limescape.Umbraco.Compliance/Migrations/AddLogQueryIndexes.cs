using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Limescape.Umbraco.Compliance.Migrations;

/// <summary>
/// Adds the indexes behind the filters of the audit and deletion-log query API: the action of <c>lsAudit</c> and the
/// person of <c>lsDeletionLog</c> (time, item and the audit person were indexed in step 1). On a fresh install step 1
/// already creates them from the DTOs; then this step does nothing.
/// </summary>
public sealed class AddLogQueryIndexes(IMigrationContext context) : AsyncMigrationBase(context)
{
    /// <inheritdoc />
    protected override Task MigrateAsync()
    {
        if (!IndexExists(AuditEntryDto.ActionIndex))
        {
            CreateIndex<AuditEntryDto>(AuditEntryDto.ActionIndex);
        }

        if (!IndexExists(DeletionLogEntryDto.ActorIndex))
        {
            CreateIndex<DeletionLogEntryDto>(DeletionLogEntryDto.ActorIndex);
        }

        return Task.CompletedTask;
    }
}
