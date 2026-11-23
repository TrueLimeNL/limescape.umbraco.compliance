using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

/// <summary>
/// Assignment of a retention class to a content or media type, a property value or an individual item.
/// Managed through <see cref="Retention.IRetentionService"/>.
/// </summary>
[TableName(ComplianceTables.RetentionAssignment)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class RetentionAssignmentDto
{
    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("retentionClassId")]
    [ForeignKey(typeof(RetentionClassDto), Name = "FK_lsRetentionAssignment_lsRetentionClass")]
    public int RetentionClassId { get; set; }

    /// <summary>contentType, propertyValue or item.</summary>
    [Column("targetType")]
    [Length(32)]
    public string TargetType { get; set; } = string.Empty;

    /// <summary>Content or media type key, for contentType and propertyValue assignments.</summary>
    [Column("contentTypeKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public Guid? ContentTypeKey { get; set; }

    [Column("propertyAlias")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? PropertyAlias { get; set; }

    [Column("propertyValue")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? PropertyValue { get; set; }

    /// <summary>Document or media key, for item assignments (override).</summary>
    [Column("itemKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Index(IndexTypes.NonClustered, Name = "IX_lsRetentionAssignment_itemKey")]
    public Guid? ItemKey { get; set; }

    [Column("createdUtc")]
    public DateTime CreatedUtc { get; set; }
}
