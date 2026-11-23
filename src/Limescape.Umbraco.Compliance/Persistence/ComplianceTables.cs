namespace Limescape.Umbraco.Compliance.Persistence;

/// <summary>
/// Names of the package tables, created by <see cref="Migrations.ComplianceMigrationPlan"/>.
/// </summary>
public static class ComplianceTables
{
    /// <summary>Append-only audit log: one row per audited change, with the acting person.</summary>
    public const string Audit = "lsAudit";

    /// <summary>Append-only deletion log: one row per trash, restore, delete or purge event.</summary>
    public const string DeletionLog = "lsDeletionLog";

    /// <summary>Approval records checked by the publication gate.</summary>
    public const string Approval = "lsApproval";

    /// <summary>Retention class definitions.</summary>
    public const string RetentionClass = "lsRetentionClass";

    /// <summary>Assignments of retention classes to content types, property values or items.</summary>
    public const string RetentionAssignment = "lsRetentionAssignment";

    /// <summary>Computed retention per item: effective period, rules and legal hold.</summary>
    public const string ItemRetention = "lsItemRetention";
}
