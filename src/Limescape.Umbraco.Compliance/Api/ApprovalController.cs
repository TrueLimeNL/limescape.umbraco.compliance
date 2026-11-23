using Asp.Versioning;
using Limescape.Umbraco.Compliance.PublicationGate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Approval of document versions for the publication gate, under
/// <c>/umbraco/management/api/v1/compliance/approval</c>. Reading the status needs access to the content or media
/// section, like reading retention; recording a decision requires membership of a reviewer group, and for an API user an
/// acting-user token that names the reviewer.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/approval")]
[ApiExplorerSettings(GroupName = "Compliance")]
public sealed class ApprovalController(IApprovalService approvals, IBackOfficeSecurityAccessor backOfficeSecurityAccessor)
    : ManagementApiControllerBase
{
    /// <summary>Approval state of the current draft: version id and hash, the decision for it, and all decisions.</summary>
    [HttpGet("document/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessContentOrMedia)]
    [ProducesResponseType<ApprovalStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult GetStatus(Guid id)
    {
        var status = approvals.GetStatus(id);
        return status is null ? DocumentNotFound() : Ok(ApprovalStatusResponse.From(status));
    }

    /// <summary>
    /// Records a decision about the current draft. Send the <c>versionHash</c> from <see cref="GetStatus"/> so a draft
    /// that changed in the meantime is not approved by accident (<c>409</c>). A rejection needs a comment or findings
    /// (<c>400</c> otherwise) and moves the lifecycle status to <c>rejected</c>, so the owner can change and resubmit.
    /// </summary>
    [HttpPost("document/{id:guid}")]
    [ProducesResponseType<ApprovalRecordResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public IActionResult Decide(Guid id, [FromBody] ApprovalDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = approvals.Decide(id, new ApprovalDecision(request.Decision ?? string.Empty, request.VersionHash, request.Comment, request.Findings));
        return result.Status switch
        {
            ApprovalOperationStatus.Success => StatusCode(StatusCodes.Status201Created, ApprovalRecordResponse.From(result.Result!)),
            ApprovalOperationStatus.DocumentNotFound => DocumentNotFound(),
            ApprovalOperationStatus.NotReviewer => Problem(StatusCodes.Status403Forbidden, GateMessages.ApprovalRequiresReviewer),
            ApprovalOperationStatus.ActingUserRequired => Problem(StatusCodes.Status403Forbidden, GateMessages.ApprovalRequiresActingUser),
            ApprovalOperationStatus.ChannelNotAllowed => Problem(StatusCodes.Status403Forbidden, GateMessages.ApprovalChannelNotAllowed),
            ApprovalOperationStatus.OwnMaterial => Problem(StatusCodes.Status403Forbidden, GateMessages.ApprovalOwnMaterial),
            ApprovalOperationStatus.VersionChanged => Problem(StatusCodes.Status409Conflict, GateMessages.ApprovalVersionChanged),
            ApprovalOperationStatus.RejectionRequiresFeedback => Problem(StatusCodes.Status400BadRequest, GateMessages.ApprovalRejectionRequiresFeedback),
            ApprovalOperationStatus.Cancelled => Problem(StatusCodes.Status409Conflict, GateMessages.ApprovalCancelled),
            _ => Problem(StatusCodes.Status400BadRequest, GateMessages.ApprovalInvalidDecision),
        };
    }

    private NotFoundObjectResult DocumentNotFound() =>
        NotFound(new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "The requested document could not be found" });

    private ObjectResult Problem(int status, string code)
    {
        var culture = GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language);
        var problem = new ProblemDetails { Status = status, Title = GateMessages.Text(code, culture) };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}

/// <summary>Body of <c>POST compliance/approval/document/{id}</c>.</summary>
public sealed class ApprovalDecisionRequest
{
    /// <summary><c>approved</c> or <c>rejected</c>.</summary>
    public string? Decision { get; set; }

    /// <summary>Hash of the version that was reviewed, from the status endpoint. Optional, recommended.</summary>
    public string? VersionHash { get; set; }

    /// <summary>Short comment, at most 2,000 characters.</summary>
    public string? Comment { get; set; }

    /// <summary>Findings of the quality check.</summary>
    public string? Findings { get; set; }
}

/// <summary>One recorded decision.</summary>
public sealed record ApprovalRecordResponse(
    long Id,
    Guid DocumentId,
    int VersionId,
    string VersionHash,
    string Decision,
    string ReviewerId,
    string? ReviewerName,
    string? Channel,
    string Source,
    string? Comment,
    string? Findings,
    DateTime DecidedUtc,
    string? CorrelationId)
{
    internal static ApprovalRecordResponse From(ApprovalRecord r) =>
        new(r.Id, r.ContentKey, r.VersionId, r.VersionHash, r.Decision, r.ReviewerId, r.ReviewerName, r.Channel, r.Source, r.Comment, r.Findings, r.DecidedUtc, r.CorrelationId);
}

/// <summary>Approval state of the current draft of a document.</summary>
public sealed record ApprovalStatusResponse(
    Guid DocumentId,
    string DocumentType,
    bool Gated,
    int VersionId,
    string VersionHash,
    bool Approved,
    ApprovalRecordResponse? CurrentDecision,
    IReadOnlyList<ApprovalRecordResponse> History)
{
    internal static ApprovalStatusResponse From(ApprovalStatus s) =>
        new(
            s.DocumentKey,
            s.DocumentType,
            s.Gated,
            s.VersionId,
            s.VersionHash,
            s.Approved,
            s.CurrentDecision is null ? null : ApprovalRecordResponse.From(s.CurrentDecision),
            s.History.Select(ApprovalRecordResponse.From).ToList());
}
