using Limescape.Umbraco.Compliance.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Reads and appends approval records in <c>lsApproval</c>. Append-only: there is no update or delete.
/// </summary>
public interface IApprovalStore
{
    /// <summary>Appends a record and returns it with its id. Joins the ambient scope if there is one.</summary>
    ApprovalRecord Add(ApprovalRecord record);

    /// <summary>The latest decision for this exact version of the document, or null.</summary>
    ApprovalRecord? GetLatest(Guid contentKey, string versionHash);

    /// <summary>All decisions about the document, newest first.</summary>
    IReadOnlyList<ApprovalRecord> GetAll(Guid contentKey);
}

/// <summary>
/// Default <see cref="IApprovalStore"/> on the Umbraco database.
/// </summary>
public sealed class ApprovalStore(IScopeProvider scopeProvider) : IApprovalStore
{
    /// <inheritdoc />
    public ApprovalRecord Add(ApprovalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var dto = new ApprovalDto
        {
            ContentKey = record.ContentKey,
            VersionId = record.VersionId,
            VersionHash = record.VersionHash,
            Decision = record.Decision,
            ReviewerId = record.ReviewerId,
            ReviewerName = Audit.AuditValues.Truncate(record.ReviewerName, 255),
            Comment = Audit.AuditValues.Truncate(record.Comment),
            Findings = record.Findings,
            Source = record.Source,
            DecidedUtc = record.DecidedUtc,
            Channel = record.Channel,
            TechnicalUserKey = record.TechnicalUserKey,
            CorrelationId = Audit.AuditValues.Truncate(record.CorrelationId, 128),
        };

        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(dto);
        scope.Complete();
        return ToRecord(dto);
    }

    /// <inheritdoc />
    public ApprovalRecord? GetLatest(Guid contentKey, string versionHash)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .Select<ApprovalDto>()
            .From<ApprovalDto>()
            .Where<ApprovalDto>(x => x.ContentKey == contentKey && x.VersionHash == versionHash)
            .OrderByDescending<ApprovalDto>(x => x.Id);
        var dto = scope.Database.Fetch<ApprovalDto>(scope.SqlContext.SqlSyntax.SelectTop(sql, 1)).FirstOrDefault();
        return dto is null ? null : ToRecord(dto);
    }

    /// <inheritdoc />
    public IReadOnlyList<ApprovalRecord> GetAll(Guid contentKey)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .Select<ApprovalDto>()
            .From<ApprovalDto>()
            .Where<ApprovalDto>(x => x.ContentKey == contentKey)
            .OrderByDescending<ApprovalDto>(x => x.Id);
        return scope.Database.Fetch<ApprovalDto>(sql).Select(ToRecord).ToList();
    }

    private static ApprovalRecord ToRecord(ApprovalDto dto) =>
        new(dto.ContentKey, dto.VersionId, dto.VersionHash ?? string.Empty, dto.Decision)
        {
            Id = dto.Id,
            ReviewerId = dto.ReviewerId,
            ReviewerName = dto.ReviewerName,
            Channel = dto.Channel,
            Source = dto.Source,
            Comment = dto.Comment,
            Findings = dto.Findings,
            DecidedUtc = DateTime.SpecifyKind(dto.DecidedUtc, DateTimeKind.Utc),
            TechnicalUserKey = dto.TechnicalUserKey,
            CorrelationId = dto.CorrelationId,
        };
}
