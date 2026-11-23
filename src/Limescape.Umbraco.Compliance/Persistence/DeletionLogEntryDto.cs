using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

[TableName(ComplianceTables.DeletionLog)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class DeletionLogEntryDto
{
    /// <summary>Index for the person filter of the query API (added by migration step 4).</summary>
    public const string ActorIndex = "IX_lsDeletionLog_actorId";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    [Column("timestampUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_lsDeletionLog_timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    /// <summary>trashed, restored, deleted, purged or purgeSkipped.</summary>
    [Column("event")]
    [Length(32)]
    public string Event { get; set; } = string.Empty;

    [Column("entityType")]
    [Length(64)]
    public string EntityType { get; set; } = string.Empty;

    [Column("entityKey")]
    [Index(IndexTypes.NonClustered, Name = "IX_lsDeletionLog_entityKey")]
    public Guid EntityKey { get; set; }

    [Column("titleSnapshot")]
    [Length(300)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? TitleSnapshot { get; set; }

    [Column("actorId")]
    [Length(255)]
    [Index(IndexTypes.NonClustered, Name = ActorIndex)]
    public string ActorId { get; set; } = string.Empty;

    [Column("actorName")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? ActorName { get; set; }

    [Column("channel")]
    [Length(32)]
    public string Channel { get; set; } = string.Empty;

    [Column("issuer")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Issuer { get; set; }

    [Column("technicalUserKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public Guid? TechnicalUserKey { get; set; }

    /// <summary>For purges: why the item was purged (restoration period ended, manual).</summary>
    [Column("reason")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Reason { get; set; }

    /// <summary>Retention class(es) that applied at the time of the event.</summary>
    [Column("retentionClass")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? RetentionClass { get; set; }

    [Column("correlationId")]
    [Length(128)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? CorrelationId { get; set; }
}
