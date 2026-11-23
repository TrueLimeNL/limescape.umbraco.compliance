using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

/// <summary>
/// Computed retention per item: the effective period, the rules it came from and the legal hold.
/// One row per document or media item that has, or had, a retention class.
/// </summary>
[TableName(ComplianceTables.ItemRetention)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class ItemRetentionDto
{
    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("itemKey")]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_lsItemRetention_itemKey")]
    public Guid ItemKey { get; set; }

    /// <summary>document or media.</summary>
    [Column("entityType")]
    [Length(16)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>End of the effective (longest) period, UTC; null when open-ended or when no class applies.</summary>
    [Column("retentionUntil")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Index(IndexTypes.NonClustered, Name = "IX_lsItemRetention_retentionUntil")]
    public DateTime? RetentionUntil { get; set; }

    /// <summary>True when a class applies whose start event has not happened yet: retention runs indefinitely for now.</summary>
    [Column("openEnded")]
    public bool OpenEnded { get; set; }

    /// <summary>Key of the class that governs the effective period.</summary>
    [Column("governingClassKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public Guid? GoverningClassKey { get; set; }

    /// <summary>Start date of the governing rule, UTC.</summary>
    [Column("startDate")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? StartDate { get; set; }

    /// <summary>True when the latest calculation gave a shorter period that was not confirmed, so an earlier, longer period is kept.</summary>
    [Column("heldFromEarlier")]
    public bool HeldFromEarlier { get; set; }

    /// <summary>Every applicable rule of the latest calculation, as JSON (class key and name, start, until, source).</summary>
    [Column("rules")]
    [SpecialDbType(SpecialDbTypes.NVARCHARMAX)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Rules { get; set; }

    /// <summary>Legal hold: blocks deletion regardless of retention. Set by administrators only.</summary>
    [Column("legalHold")]
    public bool LegalHold { get; set; }

    [Column("calculatedUtc")]
    public DateTime CalculatedUtc { get; set; }

    /// <summary>When the daily job reported that the period ended; null until then, reset when the period is extended.</summary>
    [Column("endReportedUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? EndReportedUtc { get; set; }
}
