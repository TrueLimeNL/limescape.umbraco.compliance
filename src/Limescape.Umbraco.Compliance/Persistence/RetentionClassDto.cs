using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

/// <summary>
/// Retention class, managed through <see cref="Retention.IRetentionService"/>.
/// </summary>
[TableName(ComplianceTables.RetentionClass)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class RetentionClassDto
{
    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("key")]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_lsRetentionClass_key")]
    public Guid Key { get; set; }

    [Column("name")]
    [Length(255)]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    [Length(2000)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Description { get; set; }

    [Column("durationYears")]
    public int DurationYears { get; set; }

    [Column("durationMonths")]
    public int DurationMonths { get; set; }

    [Column("durationDays")]
    public int DurationDays { get; set; }

    /// <summary>archived, published, lastModified or dateProperty.</summary>
    [Column("startEvent")]
    [Length(32)]
    public string StartEvent { get; set; } = string.Empty;

    /// <summary>Property alias when <see cref="StartEvent"/> is dateProperty.</summary>
    [Column("startPropertyAlias")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? StartPropertyAlias { get; set; }

    /// <summary>allowDeletion or notify.</summary>
    [Column("endAction")]
    [Length(32)]
    public string EndAction { get; set; } = string.Empty;

    [Column("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [Column("updatedUtc")]
    public DateTime UpdatedUtc { get; set; }
}
