using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Limescape.Umbraco.Compliance.Migrations;

/// <summary>
/// Adds the version hash, findings, channel and technical user to <c>lsApproval</c>, plus an index on content key and
/// version hash. On a fresh install step 1 already creates them from <see cref="ApprovalDto"/>; then this step does nothing.
/// </summary>
public sealed class AddApprovalVersionHash(IMigrationContext context) : AsyncMigrationBase(context)
{
    /// <inheritdoc />
    protected override Task MigrateAsync()
    {
        foreach (var column in new[] { "versionHash", "findings", "channel", "technicalUserKey" })
        {
            if (!ColumnExists(ComplianceTables.Approval, column))
            {
                AddColumn<ApprovalDto>(ComplianceTables.Approval, column);
            }
        }

        if (!IndexExists(ApprovalDto.VersionHashIndex))
        {
            CreateIndex<ApprovalDto>(ApprovalDto.VersionHashIndex);
        }

        return Task.CompletedTask;
    }
}
