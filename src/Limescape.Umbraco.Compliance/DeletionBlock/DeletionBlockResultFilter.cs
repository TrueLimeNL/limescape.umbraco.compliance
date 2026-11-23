using System.Globalization;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Limescape.Umbraco.Compliance.DeletionBlock;

/// <summary>
/// The deletion blocks of the current HTTP request, recorded by <see cref="DeletionBlockHandler"/> for <see cref="DeletionBlockResultFilter"/>.
/// </summary>
internal sealed class DeletionBlockRequestState
{
    private const string ItemKey = CompliancePackage.Id + ".DeletionBlocks";

    public List<DeletionBlock> Blocks { get; } = [];

    public List<string> Lines { get; } = [];

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static void Add(HttpContext httpContext, IEnumerable<DeletionBlock> blocks, IEnumerable<string> lines, CultureInfo culture)
    {
        if (httpContext.Items[ItemKey] is not DeletionBlockRequestState state)
        {
            state = new DeletionBlockRequestState();
            httpContext.Items[ItemKey] = state;
        }

        state.Blocks.AddRange(blocks);
        state.Lines.AddRange(lines);
        state.Culture = culture;
    }

    public static DeletionBlockRequestState? Get(HttpContext httpContext) => httpContext.Items[ItemKey] as DeletionBlockRequestState;
}

/// <summary>
/// Gives API callers a stable error: when a Management API call failed because the deletion block
/// cancelled it, the problem details get the code <see cref="DeletionBlockErrorCodes.RetentionBlocksDeletion"/>,
/// the localized message as detail and the blocked items. The status code Umbraco chose is kept.
/// </summary>
public sealed class DeletionBlockResultFilter : IAlwaysRunResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (DeletionBlockRequestState.Get(context.HttpContext) is not { } state)
        {
            return;
        }

        switch (context.Result)
        {
            case ObjectResult { Value: ProblemDetails problem }:
                Decorate(problem, state);
                break;
            case IStatusCodeActionResult { StatusCode: >= 400 } failed:
                var created = new ProblemDetails { Status = failed.StatusCode };
                Decorate(created, state);
                context.Result = new ObjectResult(created) { StatusCode = failed.StatusCode };
                break;
        }
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static void Decorate(ProblemDetails problem, DeletionBlockRequestState state)
    {
        problem.Title = DeletionBlockMessages.Category(state.Culture);
        problem.Detail = string.Join(" ", state.Lines);
        problem.Extensions["code"] = DeletionBlockErrorCodes.RetentionBlocksDeletion;
        problem.Extensions["blockedItems"] = state.Blocks
            .DistinctBy(b => b.ItemKey)
            .Select(b => new Dictionary<string, object?>
            {
                ["itemKey"] = b.ItemKey,
                ["entityType"] = b.EntityType,
                ["name"] = b.Name,
                ["reason"] = RetentionService.Storage(b.Reason),
                ["retentionUntil"] = b.RetentionUntil,
                ["retentionClass"] = b.RetentionClass,
                ["viaItemKey"] = b.ViaItemKey,
            })
            .ToList();
    }
}
