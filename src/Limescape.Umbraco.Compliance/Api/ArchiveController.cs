using Asp.Versioning;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// The archive route for the backoffice and integrations, at
/// <c>/umbraco/management/api/v1/compliance/archive/document/{id}</c>. <c>GET</c> says whether the signed-in person may
/// archive the document; <c>POST</c> archives it: status <c>archived</c>, the archive date and unpublish in one save.
/// Owners may archive their own material; administrators and the retention manager groups
/// (<c>Compliance:Retention:ManagerGroups</c>) act as functional manager. An API user must send an acting-user token for
/// the person who confirmed the archive. Needs access to the content section.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/archive")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class ArchiveController(
    IArchiveService archiveService,
    IContentService contentService,
    IActingUserAccessor actingUserAccessor,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    IOptionsMonitor<RetentionOptions> retentionOptions) : ManagementApiControllerBase
{
    /// <summary>Error code: the document does not exist.</summary>
    public const string NotFoundCode = "archive_document_not_found";

    /// <summary>Error code: the reason is longer than <see cref="ArchiveService.MaxReasonLength"/> characters.</summary>
    public const string InvalidCode = "archive_invalid";

    /// <summary>Archive state of the document for the signed-in person.</summary>
    [HttpGet("document/{id:guid}")]
    public IActionResult GetState(Guid id) =>
        archiveService.GetState(id, actingUserAccessor.Current, IsManager()) is { } state
            ? ComplianceApiResults.Json(state)
            : ComplianceApiResults.Problem(StatusCodes.Status404NotFound, NotFoundCode, "The document does not exist.");

    /// <summary>Archives the document; the person signed in (or named in the acting-user token) confirms it.</summary>
    [HttpPost("document/{id:guid}")]
    public IActionResult Archive(Guid id, [FromBody] ArchiveRequest? request)
    {
        if (request?.Reason is { Length: > ArchiveService.MaxReasonLength })
        {
            return ComplianceApiResults.Problem(StatusCodes.Status400BadRequest, InvalidCode, $"reason is longer than {ArchiveService.MaxReasonLength} characters.");
        }

        var result = archiveService.Archive(id, actingUserAccessor.Current, IsManager(), request?.Reason);
        var name = contentService.GetById(id)?.Name;
        return result.Status switch
        {
            ArchiveOperationStatus.Success => ComplianceApiResults.Json(result.Result!),
            ArchiveOperationStatus.DocumentNotFound => ComplianceApiResults.Problem(StatusCodes.Status404NotFound, NotFoundCode, "The document does not exist."),
            ArchiveOperationStatus.ActingUserRequired => Problem(StatusCodes.Status403Forbidden, GateMessages.ArchiveRequiresActingUser, name),
            ArchiveOperationStatus.NotOwner => Problem(StatusCodes.Status403Forbidden, GateMessages.ArchiveNotOwner, name),
            ArchiveOperationStatus.NotSupported => Problem(StatusCodes.Status400BadRequest, GateMessages.ArchiveNotSupported, name),
            ArchiveOperationStatus.NotPublished => Problem(StatusCodes.Status409Conflict, GateMessages.ArchiveRequiresPublished, name),
            ArchiveOperationStatus.AlreadyArchived => Problem(StatusCodes.Status409Conflict, GateMessages.ArchiveAlreadyArchived, name),
            _ => Problem(StatusCodes.Status409Conflict, GateMessages.ArchiveCancelled, name),
        };
    }

    /// <summary>Administrators and the retention manager groups act as functional manager for archiving.</summary>
    private bool IsManager() =>
        backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser is { } user && RetentionPolicies.CanManage(user, retentionOptions.CurrentValue);

    private ContentResult Problem(int status, string code, string? name) => ComplianceApiResults.Problem(
        status, code, GateMessages.Text(code, GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language), name));
}

/// <summary>Body of <c>POST compliance/archive/document/{id}</c>.</summary>
public sealed class ArchiveRequest
{
    /// <summary>Optional reason, written to the audit log (max 500 characters).</summary>
    public string? Reason { get; set; }
}
