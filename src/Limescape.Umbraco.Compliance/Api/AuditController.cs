using System.Globalization;
using Asp.Versioning;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Query API for the audit log and the deletion log, at <c>/umbraco/management/api/v1/compliance/audit</c>
/// and <c>/compliance/deletion-log</c>. Filters: <c>person</c>, <c>item</c>, <c>action</c> (deletion log: the event),
/// <c>entityType</c>, <c>channel</c>, <c>from</c> and <c>to</c> (UTC, ISO 8601; to is exclusive); paging with
/// <c>skip</c> and <c>take</c> (max 500). Newest first. The history of one item (<c>item</c> set) needs access to the
/// content or media section; the whole log needs a retention manager (administrator or a group in
/// <c>Compliance:Retention:ManagerGroups</c>), because it names every person who changed anything.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContentOrMedia)]
public sealed class AuditController(
    IAuditLogReader reader,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    IOptionsMonitor<RetentionOptions> retentionOptions) : ManagementApiControllerBase
{
    /// <summary>Error code: querying the whole log needs a retention manager.</summary>
    public const string RequiresManagerCode = "audit_requires_manager";

    /// <summary>Error code: a filter value is invalid.</summary>
    public const string InvalidFilterCode = "audit_invalid_filter";

    /// <summary>Audit entries, newest first.</summary>
    [HttpGet("audit")]
    public IActionResult GetAudit(
        [FromQuery] string? person = null,
        [FromQuery] Guid? item = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100) =>
        Query(person, item, action, entityType, channel, from, to, skip, take, filter => reader.QueryAudit(filter));

    /// <summary>Deletion-log events (trashed, restored, deleted, purged), newest first; <c>action</c> filters on the event.</summary>
    [HttpGet("deletion-log")]
    public IActionResult GetDeletionLog(
        [FromQuery] string? person = null,
        [FromQuery] Guid? item = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100) =>
        Query(person, item, action, entityType, channel, from, to, skip, take, filter => reader.QueryDeletionLog(filter));

    private ContentResult Query<T>(
        string? person,
        Guid? item,
        string? action,
        string? entityType,
        string? channel,
        string? from,
        string? to,
        int skip,
        int take,
        Func<AuditLogFilter, LogPage<T>> query)
    {
        if (item is null && !CanReadWholeLog())
        {
            return ComplianceApiResults.Problem(
                StatusCodes.Status403Forbidden,
                RequiresManagerCode,
                "Only a retention manager can query the whole log; pass an item to read the history of one item.");
        }

        if (!TryParseUtc(from, out var fromUtc) || !TryParseUtc(to, out var toUtc))
        {
            return ComplianceApiResults.Problem(StatusCodes.Status400BadRequest, InvalidFilterCode, "from and to must be ISO 8601 dates or date-times.");
        }

        if (skip < 0 || take is < 1 or > AuditLogReader.MaxTake)
        {
            return ComplianceApiResults.Problem(StatusCodes.Status400BadRequest, InvalidFilterCode, $"skip must be 0 or more and take between 1 and {AuditLogReader.MaxTake}.");
        }

        return ComplianceApiResults.Json(query(new AuditLogFilter
        {
            Person = Blank(person),
            Item = item,
            Action = Blank(action),
            EntityType = Blank(entityType),
            Channel = Blank(channel),
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Skip = skip,
            Take = take,
        }));
    }

    private bool CanReadWholeLog() =>
        backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser is { } user && RetentionPolicies.CanManage(user, retentionOptions.CurrentValue);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseUtc(string? value, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        return false;
    }
}
