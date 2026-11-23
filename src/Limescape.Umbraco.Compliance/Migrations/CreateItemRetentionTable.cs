using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Limescape.Umbraco.Compliance.Migrations;

/// <summary>
/// Creates <c>lsItemRetention</c>, the computed retention per item. Idempotent.
/// </summary>
public sealed class CreateItemRetentionTable(IMigrationContext context) : AsyncMigrationBase(context)
{
    /// <inheritdoc />
    protected override Task MigrateAsync()
    {
        if (!SqlSyntax.DoesTableExist(Database, ComplianceTables.ItemRetention))
        {
            Create.Table<ItemRetentionDto>().Do();
        }

        return Task.CompletedTask;
    }
}
