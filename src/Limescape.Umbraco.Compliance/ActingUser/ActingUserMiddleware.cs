using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Limescape.Umbraco.Compliance.ActingUser;

/// <summary>
/// Validates the acting-user header at the start of the Umbraco pipeline. A request without the header passes
/// unchanged (the acting person is then the Umbraco user). A request with an invalid header is rejected with 401
/// before any endpoint runs, so nothing is written under the wrong identity.
/// </summary>
public sealed partial class ActingUserMiddleware(
    RequestDelegate next,
    IActingUserTokenValidator validator,
    IOptionsMonitor<ActingUserOptions> options,
    ILogger<ActingUserMiddleware> logger)
{
    /// <summary>Error code in the 401 response body.</summary>
    public const string InvalidTokenCode = "acting_user_invalid";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headerName = options.CurrentValue.HeaderName;
        if (!context.Request.Headers.TryGetValue(headerName, out var values))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var result = values.Count == 1
            ? await validator.ValidateAsync(values[0] ?? string.Empty).ConfigureAwait(false)
            : ActingUserTokenValidationResult.Failure("header must occur exactly once");

        if (!result.IsValid)
        {
            LogRejected(logger, headerName, context.Request.Method, context.Request.Path, result.Error);
            await RejectAsync(context, headerName).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new ActingUserTokenFeature(result.Token!));
        await next(context).ConfigureAwait(false);
    }

    private static Task RejectAsync(HttpContext context, string headerName)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Invalid acting-user token",
            Detail = $"The {headerName} header is present but is not a valid token from a trusted issuer.",
        };
        problem.Extensions["code"] = InvalidTokenCode;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected {Header} on {Method} {Path}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string header, string method, PathString path, string? reason);
}
