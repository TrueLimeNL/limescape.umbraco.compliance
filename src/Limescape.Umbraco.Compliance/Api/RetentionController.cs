using System.Text.Json;
using Asp.Versioning;
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
/// Management API extension for retention, at
/// <c>/umbraco/management/api/v1/compliance/retention</c>. Reading needs access to the content or media section;
/// changing classes and assignments needs <see cref="RetentionPolicies.Manage"/>; confirming a shorter period
/// needs an administrator. Responses use camelCase JSON with enum values as camelCase strings.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("compliance/retention")]
[ApiExplorerSettings(GroupName = "Compliance")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContentOrMedia)]
public sealed class RetentionController(
    IRetentionService retentionService,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    TimeProvider timeProvider) : ManagementApiControllerBase
{
    private const string ShorteningDetail = "Only an administrator can confirm a shorter retention period.";

    /// <summary>All retention classes.</summary>
    [HttpGet("class")]
    public IActionResult GetClasses() => JsonResult(retentionService.GetClasses());

    /// <summary>One retention class.</summary>
    [HttpGet("class/{key:guid}")]
    public IActionResult GetClass(Guid key) =>
        retentionService.GetClass(key) is { } cls ? JsonResult(cls) : Problem404(RetentionErrorCodes.ClassNotFound, "The retention class does not exist.");

    /// <summary>Creates a retention class.</summary>
    [HttpPost("class")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult CreateClass([FromBody] RetentionClassRequest request) => Guarded(() =>
        JsonResult(retentionService.CreateClass(ToInput(request)), StatusCodes.Status201Created));

    /// <summary>Updates a class and recalculates the items it applies to. A shorter period needs <paramref name="confirmShortening"/> by an administrator.</summary>
    [HttpPut("class/{key:guid}")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult UpdateClass(Guid key, [FromBody] RetentionClassRequest request, [FromQuery] bool confirmShortening = false) => Guarded(() =>
    {
        if (confirmShortening && !IsAdministrator())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, RetentionErrorCodes.ShorteningRequiresAdministrator, ShorteningDetail);
        }

        return retentionService.UpdateClass(key, ToInput(request), confirmShortening) is { } updated
            ? JsonResult(new { @class = updated.Class, recalculation = updated.Recalculation })
            : Problem404(RetentionErrorCodes.ClassNotFound, "The retention class does not exist.");
    });

    /// <summary>Deletes a class that is not assigned.</summary>
    [HttpDelete("class/{key:guid}")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult DeleteClass(Guid key) => Guarded(() =>
        retentionService.DeleteClass(key) ? Ok() : Problem404(RetentionErrorCodes.ClassNotFound, "The retention class does not exist."));

    /// <summary>All assignments.</summary>
    [HttpGet("assignment")]
    public IActionResult GetAssignments() => JsonResult(retentionService.GetAssignments());

    /// <summary>Assigns a class to a document or media type, a property value (category) or one item; calculates the items it applies to.</summary>
    [HttpPost("assignment")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult CreateAssignment([FromBody] RetentionAssignmentRequest request) => Guarded(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = ParseEnum<RetentionAssignmentTarget>(request.Target, "target");
        var (assignment, recalculation) = retentionService.CreateAssignment(new RetentionAssignment(0, request.ClassKey, target)
        {
            ContentTypeKey = request.ContentTypeKey,
            PropertyAlias = request.PropertyAlias,
            PropertyValue = request.PropertyValue,
            ItemKey = request.ItemKey,
        });
        return JsonResult(new { assignment, recalculation }, StatusCodes.Status201Created);
    });

    /// <summary>Removes an assignment and recalculates the items it applied to. Dropping or shortening a period needs <paramref name="confirmShortening"/> by an administrator.</summary>
    [HttpDelete("assignment/{id:int}")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult DeleteAssignment(int id, [FromQuery] bool confirmShortening = false)
    {
        if (confirmShortening && !IsAdministrator())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, RetentionErrorCodes.ShorteningRequiresAdministrator, ShorteningDetail);
        }

        return retentionService.DeleteAssignment(id, confirmShortening) is { } recalculation
            ? JsonResult(new { recalculation })
            : Problem404(RetentionErrorCodes.Invalid, "The assignment does not exist.");
    }

    /// <summary>Retention of one document or media item: classes with their rule, start, end, status and legal hold.</summary>
    [HttpGet("item/{key:guid}")]
    public IActionResult GetItem(Guid key) =>
        retentionService.GetItem(key) is { } info ? JsonResult(info) : Problem404(RetentionErrorCodes.ItemNotFound, "The item does not exist.");

    /// <summary>Recalculates one item. A shorter period needs <paramref name="confirmShortening"/> by an administrator.</summary>
    [HttpPost("item/{key:guid}/recalculate")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult RecalculateItem(Guid key, [FromQuery] bool confirmShortening = false)
    {
        if (confirmShortening && !IsAdministrator())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, RetentionErrorCodes.ShorteningRequiresAdministrator, ShorteningDetail);
        }

        return retentionService.RecalculateItem(key, confirmShortening) is { } recalculation
            ? JsonResult(new { recalculation, item = retentionService.GetItem(key) })
            : Problem404(RetentionErrorCodes.ItemNotFound, "The item does not exist.");
    }

    /// <summary>Items whose retention period has ended, oldest end first.</summary>
    [HttpGet("ended")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult GetEnded([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        var items = retentionService.GetEnded(timeProvider.GetUtcNow().UtcDateTime, Math.Max(0, skip), Math.Clamp(take, 1, 500));
        return JsonResult(new { skip, take = items.Count, items });
    }

    /// <summary>Items whose period runs: started (for example archived) and not ended, with start and end, earliest end first.</summary>
    [HttpGet("running")]
    [Authorize(Policy = RetentionPolicies.Manage)]
    public IActionResult GetRunning([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        var items = retentionService.GetRunning(timeProvider.GetUtcNow().UtcDateTime, Math.Max(0, skip), Math.Clamp(take, 1, 500));
        return JsonResult(new { skip, take = items.Count, items });
    }

    private bool IsAdministrator() => backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.IsAdmin() == true;

    private static IActionResult Guarded(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (RetentionValidationException ex)
        {
            var status = ex.Code == RetentionErrorCodes.ClassInUse ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return ProblemResult(status, ex.Code, ex.Message);
        }
    }

    private static RetentionClassInput ToInput(RetentionClassRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RetentionClassInput(
            request.Name ?? string.Empty,
            new RetentionDuration(request.DurationYears, request.DurationMonths, request.DurationDays),
            ParseEnum<RetentionStartEvent>(request.StartEvent, "startEvent"),
            ParseEnum<RetentionEndAction>(request.EndAction, "endAction"))
        {
            Description = request.Description,
            StartPropertyAlias = request.StartPropertyAlias,
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, string name)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && !int.TryParse(value, out _) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new RetentionValidationException(
                RetentionErrorCodes.Invalid,
                $"{name} must be one of: {string.Join(", ", Enum.GetValues<TEnum>().Select(RetentionService.Storage))}.");

    private static ContentResult JsonResult(object value, int status = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, RetentionService.Json),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status,
    };

    private static ContentResult Problem404(string code, string detail) => ProblemResult(StatusCodes.Status404NotFound, code, detail);

    private static ContentResult ProblemResult(int status, string code, string detail) => new()
    {
        Content = JsonSerializer.Serialize(new { status, title = ReasonPhrases.GetReasonPhrase(status), detail, code }, RetentionService.Json),
        ContentType = "application/problem+json; charset=utf-8",
        StatusCode = status,
    };
}
