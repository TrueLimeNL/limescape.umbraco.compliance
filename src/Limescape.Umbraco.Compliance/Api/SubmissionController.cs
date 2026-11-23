using System.Globalization;
using Asp.Versioning;
using Limescape.Umbraco.Compliance.Submission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Submission for review, at <c>/umbraco/management/api/v1/compliance/submission/document/{id}</c>.
/// <c>GET</c> lists the empty required fields; <c>POST</c> refuses with <c>400</c> and
/// <see cref="SubmissionErrorCodes.MissingRequiredFields"/> while required fields are empty, otherwise sets the status
/// (<c>Compliance:Submission:StatusProperty</c>) to submitted. Needs access to the content section.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/submission")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class SubmissionController(
    ISubmissionGuard guard,
    IContentService contentService,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor) : ManagementApiControllerBase
{
    /// <summary>Error code: the document does not exist.</summary>
    public const string NotFoundCode = "submission_document_not_found";

    /// <summary>Error code: saving the status was cancelled by another rule.</summary>
    public const string CancelledCode = "submission_cancelled";

    /// <summary>Whether the current draft can be submitted, and which required fields are empty.</summary>
    [HttpGet("document/{id:guid}")]
    public IActionResult GetStatus(Guid id)
    {
        var content = contentService.GetById(id);
        if (content is null)
        {
            return ComplianceApiResults.Problem(StatusCodes.Status404NotFound, NotFoundCode, "The document does not exist.");
        }

        var missing = guard.Check(content, Culture());
        return ComplianceApiResults.Json(new
        {
            documentId = id,
            documentType = content.ContentType.Alias,
            submitted = guard.IsSubmitted(content),
            ready = missing.Count == 0,
            missingFields = missing,
        });
    }

    /// <summary>Submits the current draft for review.</summary>
    [HttpPost("document/{id:guid}")]
    public IActionResult Submit(Guid id)
    {
        var culture = Culture();
        var userId = backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Id ?? -1;
        var result = guard.Submit(id, culture, userId);
        return result.Status switch
        {
            SubmissionStatus.Submitted => ComplianceApiResults.Json(new { documentId = id, submitted = true }),
            SubmissionStatus.NotFound => ComplianceApiResults.Problem(StatusCodes.Status404NotFound, NotFoundCode, "The document does not exist."),
            SubmissionStatus.MissingRequiredFields => ComplianceApiResults.Problem(
                StatusCodes.Status400BadRequest,
                SubmissionErrorCodes.MissingRequiredFields,
                SubmissionMessages.MissingFields(result.MissingFields, culture),
                new Dictionary<string, object?> { ["missingFields"] = result.MissingFields }),
            _ => ComplianceApiResults.Problem(StatusCodes.Status409Conflict, CancelledCode, result.Message ?? "Saving the status was cancelled."),
        };
    }

    /// <summary>For an API user the request's Accept-Language, otherwise the backoffice user's language; English by default.</summary>
    private CultureInfo Culture()
    {
        var user = backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        var candidates = new List<string?>();
        if (user is null or { Kind: global::Umbraco.Cms.Core.Models.Membership.UserKind.Api })
        {
            candidates.AddRange(HttpContext.Request.GetTypedHeaders().AcceptLanguage.OrderByDescending(l => l.Quality ?? 1).Select(l => l.Value.Value));
        }

        candidates.Add(user?.Language);
        foreach (var name in candidates)
        {
            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("nl", StringComparison.OrdinalIgnoreCase))
            {
                return CultureInfo.GetCultureInfo("nl");
            }

            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return CultureInfo.GetCultureInfo("en");
            }
        }

        return CultureInfo.GetCultureInfo("en");
    }
}
