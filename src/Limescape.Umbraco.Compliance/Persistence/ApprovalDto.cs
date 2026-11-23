using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Limescape.Umbraco.Compliance.Persistence;

/// <summary>
/// Approval record for one document version: one row per decision, never updated. The publication gate
/// lets a version through when the latest decision for its content key and version hash is <c>approved</c>.
/// </summary>
[TableName(ComplianceTables.Approval)]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
internal sealed class ApprovalDto
{
    /// <summary>Index on content key and version hash, added in <see cref="Migrations.AddApprovalVersionHash"/>.</summary>
    public const string VersionHashIndex = "IX_lsApproval_contentKey_versionHash";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    [Column("contentKey")]
    [Index(IndexTypes.NonClustered, Name = "IX_lsApproval_contentKey_versionId", ForColumns = "contentKey,versionId")]
    public Guid ContentKey { get; set; }

    /// <summary>Umbraco version id of the draft that was approved. Kept for traceability; the gate matches on the hash.</summary>
    [Column("versionId")]
    public int VersionId { get; set; }

    /// <summary>SHA-256 of the draft content (<see cref="PublicationGate.IContentVersionHasher"/>). Null only for rows written before version hashes were stored.</summary>
    [Column("versionHash")]
    [Length(64)]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Index(IndexTypes.NonClustered, Name = VersionHashIndex, ForColumns = "contentKey,versionHash")]
    public string? VersionHash { get; set; }

    /// <summary>approved or rejected.</summary>
    [Column("decision")]
    [Length(16)]
    public string Decision { get; set; } = string.Empty;

    [Column("reviewerId")]
    [Length(255)]
    public string ReviewerId { get; set; } = string.Empty;

    [Column("reviewerName")]
    [Length(255)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? ReviewerName { get; set; }

    [Column("comment")]
    [Length(2000)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Comment { get; set; }

    /// <summary>Findings of the quality check, free text (for example a list of remarks).</summary>
    [Column("findings")]
    [SpecialDbType(SpecialDbTypes.NVARCHARMAX)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Findings { get; set; }

    /// <summary>backoffice, api, integration or workflow; see <see cref="PublicationGate.ApprovalSources"/>.</summary>
    [Column("source")]
    [Length(32)]
    public string Source { get; set; } = string.Empty;

    [Column("decidedUtc")]
    public DateTime DecidedUtc { get; set; }

    /// <summary>Channel of the reviewer (from the acting-user token, or backoffice/api).</summary>
    [Column("channel")]
    [Length(32)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Channel { get; set; }

    /// <summary>The authenticated Umbraco user that carried out the request (for example an API user).</summary>
    [Column("technicalUserKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public Guid? TechnicalUserKey { get; set; }

    [Column("correlationId")]
    [Length(128)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? CorrelationId { get; set; }
}
