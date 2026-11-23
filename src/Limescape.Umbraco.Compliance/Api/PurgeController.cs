using Asp.Versioning;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Purge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Purge of the recycle bin after a restoration period, at
/// <c>POST /umbraco/management/api/v1/compliance/purge</c>. Needs a retention manager (administrator or a group in
/// <c>Compliance:Retention:ManagerGroups</c>). Items under retention or legal hold are never deleted: they are skipped,
/// listed with their blocking items and audited.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/purge")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContentOrMedia)]
[Authorize(Policy = RetentionPolicies.Manage)]
public sealed class PurgeController(IPurgeService purgeService, IBackOfficeSecurityAccessor backOfficeSecurityAccessor) : ManagementApiControllerBase
{
    /// <summary>Error code: the request is invalid.</summary>
    public const string InvalidCode = "purge_invalid";

    /// <summary>Largest restoration period, in days (100 years).</summary>
    public const int MaxRestorationPeriodDays = 36500;

    /// <summary>
    /// Deletes the top-level recycle-bin items trashed at least <see cref="PurgeRequest.RestorationPeriodDays"/> days ago,
    /// or with <see cref="PurgeRequest.DryRun"/> only lists them.
    /// </summary>
    [HttpPost]
    public IActionResult Purge([FromBody] PurgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RestorationPeriodDays is not { } days || days is < 0 or > MaxRestorationPeriodDays)
        {
            return ComplianceApiResults.Problem(StatusCodes.Status400BadRequest, InvalidCode, $"restorationPeriodDays is required, between 0 and {MaxRestorationPeriodDays}.");
        }

        var entityTypes = request.EntityTypes is { Count: > 0 } requested ? requested : [AuditEntityTypes.Document, AuditEntityTypes.Media];
        if (entityTypes.Any(t => t is not (AuditEntityTypes.Document or AuditEntityTypes.Media)))
        {
            return ComplianceApiResults.Problem(StatusCodes.Status400BadRequest, InvalidCode, "entityTypes may contain document and media.");
        }

        var userId = backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Id ?? -1;
        return ComplianceApiResults.Json(purgeService.Purge(TimeSpan.FromDays(days), request.DryRun, [.. entityTypes], userId));
    }
}

/// <summary>Body of <c>POST compliance/purge</c>.</summary>
public sealed class PurgeRequest
{
    /// <summary>Restoration period in days: items trashed at least this long ago are purged. Required; 0 purges every free item in the bin.</summary>
    public int? RestorationPeriodDays { get; set; }

    /// <summary>True to only list what would be purged and skipped.</summary>
    public bool DryRun { get; set; }

    /// <summary><c>document</c>, <c>media</c> or both (default).</summary>
    public IList<string>? EntityTypes { get; set; }
}
