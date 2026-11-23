using System.Text.Json;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// JSON and problem responses of the compliance controllers: camelCase, enums as camelCase strings, problems with a
/// stable <c>code</c>.
/// </summary>
internal static class ComplianceApiResults
{
    public static ContentResult Json(object value, int status = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, RetentionService.Json),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status,
    };

    public static ContentResult Problem(int status, string code, string detail, IDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["title"] = ReasonPhrases.GetReasonPhrase(status),
            ["detail"] = detail,
            ["code"] = code,
        };
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            body[key] = value;
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(body, RetentionService.Json),
            ContentType = "application/problem+json; charset=utf-8",
            StatusCode = status,
        };
    }
}
