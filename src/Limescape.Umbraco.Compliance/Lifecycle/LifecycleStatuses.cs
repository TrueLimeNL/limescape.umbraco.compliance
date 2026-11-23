using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.Compliance.Lifecycle;

/// <summary>
/// Values of the lifecycle status: draft, submitted, inReview, rejected, approved, published,
/// archived. The status is a copy kept by the content layer; the routes that move it are the submission guard, a review
/// decision, the publication gate and the archive route.
/// </summary>
public static class LifecycleStatuses
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string InReview = "inReview";
    public const string Rejected = "rejected";
    public const string Approved = "approved";
    public const string Published = "published";
    public const string Archived = "archived";

    /// <summary>The status code in a stored value: a JSON array of codes (dropdown) or a plain string. Null when empty.</summary>
    public static string? Read(object? value)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (!text.StartsWith('['))
        {
            return text;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(text)?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>The status of the draft of <paramref name="content"/>; null when the property is missing or empty.</summary>
    public static string? Of(IContentBase content, string alias) =>
        content.HasProperty(alias) ? Read(content.GetValue(alias)) : null;

    /// <summary>Sets the status in the same form as the stored value: a JSON array for a dropdown, otherwise a plain string.</summary>
    public static void Write(IContentBase content, string alias, string status)
    {
        ArgumentNullException.ThrowIfNull(content);
        var property = content.Properties[alias] ?? throw new InvalidOperationException($"The document has no property '{alias}'.");
        var stored = property.GetValue();
        object value = stored is string s && s.TrimStart().StartsWith('[')
            || property.PropertyType.PropertyEditorAlias == Constants.PropertyEditors.Aliases.DropDownListFlexible
            ? JsonSerializer.Serialize(new[] { status })
            : status;
        content.SetValue(alias, value);
    }
}
