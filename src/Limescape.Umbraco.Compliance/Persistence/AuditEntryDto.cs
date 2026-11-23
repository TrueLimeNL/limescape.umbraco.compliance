using Limescape.Umbraco.Compliance.Audit;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

[TableName(ComplianceTables.Audit)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class AuditEntryDto
{
    /// <summary>Index for the action filter of the query API (added by migration step 4).</summary>
    public const string ActionIndex = "IX_lsAudit_action";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    [Column("timestampUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_lsAudit_timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    /// <summary>Person id of the acting person: from the acting-user token, or the Umbraco user key.</summary>
    [Column("actorId")]
    [Length(255)]
    [Index(IndexTypes.NonClustered, Name = "IX_lsAudit_actorId")]
    public string ActorId { get; set; } = string.Empty;

    [Column("actorName")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? ActorName { get; set; }

    [Column("channel")]
    [Length(32)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Issuer of the acting-user token; null when the actor is the Umbraco user itself.</summary>
    [Column("issuer")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Issuer { get; set; }

    /// <summary>The authenticated Umbraco user (for example an API user) that carried out the request.</summary>
    [Column("technicalUserKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public Guid? TechnicalUserKey { get; set; }

    [Column("technicalUserName")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? TechnicalUserName { get; set; }

    [Column("action")]
    [Length(64)]
    [Index(IndexTypes.NonClustered, Name = ActionIndex)]
    public string Action { get; set; } = string.Empty;

    [Column("entityType")]
    [Length(64)]
    public string EntityType { get; set; } = string.Empty;

    [Column("entityKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Index(IndexTypes.NonClustered, Name = "IX_lsAudit_entityKey")]
    public Guid? EntityKey { get; set; }

    [Column("field")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Field { get; set; }

    [Column("oldValue")]
    [Length(AuditValues.MaxLength)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? OldValue { get; set; }

    [Column("newValue")]
    [Length(AuditValues.MaxLength)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? NewValue { get; set; }

    [Column("correlationId")]
    [Length(128)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? CorrelationId { get; set; }
}
