using System.Text.Json;
using Asp.Versioning;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Legal hold per item, at <c>PUT /umbraco/management/api/v1/compliance/retention/item/{key}/legal-hold</c>.
/// Only administrators may set or lift a hold; the retention manager groups may not. Every change is audited.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/retention")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContentOrMedia)]
public sealed class LegalHoldController(IDeletionBlockService deletionBlockService, IBackOfficeSecurityAccessor backOfficeSecurityAccessor)
    : ManagementApiControllerBase
{
    /// <summary>Sets or lifts the legal hold of a document or media item; returns the item's retention.</summary>
    [HttpPut("item/{key:guid}/legal-hold")]
    public IActionResult SetLegalHold(Guid key, [FromBody] LegalHoldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.IsAdmin() != true)
        {
            return ProblemResult(StatusCodes.Status403Forbidden, DeletionBlockErrorCodes.LegalHoldRequiresAdministrator, "Only an administrator can set or lift a legal hold.");
        }

        if (request.Reason is { Length: > 500 })
        {
            return ProblemResult(StatusCodes.Status400BadRequest, RetentionErrorCodes.Invalid, "reason is longer than 500 characters.");
        }

        return deletionBlockService.SetLegalHold(key, request.LegalHold, request.Reason) is { } info
            ? new ContentResult
            {
                Content = JsonSerializer.Serialize(info, RetentionService.Json),
                ContentType = "application/json; charset=utf-8",
                StatusCode = StatusCodes.Status200OK,
            }
            : ProblemResult(StatusCodes.Status404NotFound, RetentionErrorCodes.ItemNotFound, "The item does not exist.");
    }

    private static ContentResult ProblemResult(int status, string code, string detail) => new()
    {
        Content = JsonSerializer.Serialize(new { status, title = ReasonPhrases.GetReasonPhrase(status), detail, code }, RetentionService.Json),
        ContentType = "application/problem+json; charset=utf-8",
        StatusCode = status,
    };
}

/// <summary>Body for setting or lifting a legal hold.</summary>
public sealed class LegalHoldRequest
{
    /// <summary>True to set the hold, false to lift it.</summary>
    public bool LegalHold { get; set; }

    /// <summary>Optional reason, for example a case number (max 500 characters); written to the audit log.</summary>
    public string? Reason { get; set; }
}
