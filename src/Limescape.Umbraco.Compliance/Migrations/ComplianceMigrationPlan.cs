using Umbraco.Cms.Core.Packaging;

namespace Limescape.Umbraco.Compliance.Migrations;

/// <summary>
/// Package migration plan. Umbraco discovers it by type and runs pending steps at startup
/// (unattended unless <c>Umbraco:CMS:Unattended:PackageMigrationsUnattended</c> is false).
/// Never change a step that has been released; add a new one.
/// </summary>
public sealed class ComplianceMigrationPlan : PackageMigrationPlan
{
    /// <summary>State after the first step; also the key Umbraco stores per installation.</summary>
    public const string CreateTablesState = "ls-compliance-001-create-tables";

    /// <summary>State after the item retention table was added.</summary>
    public const string ItemRetentionState = "ls-compliance-002-item-retention";

    /// <summary>State after the approval columns for the publication gate.</summary>
    public const string ApprovalVersionHashState = "ls-compliance-003-approval-version-hash";

    /// <summary>State after the indexes for the audit and deletion-log query API.</summary>
    public const string LogQueryIndexesState = "ls-compliance-004-log-query-indexes";

    public ComplianceMigrationPlan()
        : base(CompliancePackage.Id, CompliancePackage.Id, CompliancePackage.Id)
    {
    }

    /// <inheritdoc />
    protected override void DefinePlan()
    {
        To<CreateComplianceTables>(CreateTablesState);
        To<CreateItemRetentionTable>(ItemRetentionState);
        To<AddApprovalVersionHash>(ApprovalVersionHashState);
        To<AddLogQueryIndexes>(LogQueryIndexesState);
    }
}
