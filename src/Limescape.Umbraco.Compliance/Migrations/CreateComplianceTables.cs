using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Limescape.Umbraco.Compliance.Migrations;

/// <summary>
/// Creates the package tables. Idempotent: tables that already exist are left alone.
/// </summary>
public sealed class CreateComplianceTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    /// <inheritdoc />
    protected override Task MigrateAsync()
    {
        CreateIfMissing<AuditEntryDto>(ComplianceTables.Audit);
        CreateIfMissing<DeletionLogEntryDto>(ComplianceTables.DeletionLog);
        CreateIfMissing<ApprovalDto>(ComplianceTables.Approval);
        CreateIfMissing<RetentionClassDto>(ComplianceTables.RetentionClass);
        CreateIfMissing<RetentionAssignmentDto>(ComplianceTables.RetentionAssignment);
        return Task.CompletedTask;
    }

    private void CreateIfMissing<TDto>(string tableName)
    {
        if (!SqlSyntax.DoesTableExist(Database, tableName))
        {
            Create.Table<TDto>().Do();
        }
    }
}
