using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Submission;

/// <summary>
/// Checks required fields before a document can be submitted for review, and submits it.
/// </summary>
public interface ISubmissionGuard
{
    /// <summary>
    /// Required fields of <paramref name="content"/> that are empty; empty when it can be submitted. Labels are in
    /// <paramref name="culture"/> when the provider has them (default English).
    /// </summary>
    IReadOnlyList<MissingField> Check(IContent content, CultureInfo? culture = null);

    /// <summary>
    /// Aliases that must be filled before a document of the type <paramref name="documentTypeAlias"/> can be submitted:
    /// the union of what every provider requires for that type. The same rule as <see cref="Check"/>, without a document.
    /// </summary>
    IReadOnlyList<RequiredField> RequiredFieldsFor(string documentTypeAlias) => [];

    /// <summary>True when the status of <paramref name="content"/> is one of the submitted values.</summary>
    bool IsSubmitted(IContent content);

    /// <summary>
    /// Validates and, when <see cref="SubmissionOptions.StatusProperty"/> is set, sets the status to submitted and saves.
    /// </summary>
    SubmissionResult Submit(Guid documentKey, CultureInfo? culture = null, int userId = -1);
}

/// <summary>An empty required field.</summary>
/// <param name="Alias">Property alias.</param>
/// <param name="Label">Label in the requested language: from the field itself or a <see cref="IFieldLabelProvider"/>, else the property name, else the alias. Never a localization key.</param>
/// <param name="Culture">The culture whose value is empty, for properties that vary by culture.</param>
/// <param name="Labels">The label per language (for example nl, en), so a consumer does not have to read the message.</param>
public sealed record MissingField(string Alias, string Label, string? Culture = null, IReadOnlyDictionary<string, string>? Labels = null);

/// <summary>Outcome of <see cref="ISubmissionGuard.Submit"/>.</summary>
public sealed record SubmissionResult(SubmissionStatus Status, IReadOnlyList<MissingField> MissingFields, string? Message = null);

/// <summary>Status of a submission.</summary>
public enum SubmissionStatus
{
    /// <summary>All required fields are filled; the status was set (or there is no status property).</summary>
    Submitted,

    /// <summary>Required fields are empty; nothing was changed.</summary>
    MissingRequiredFields,

    /// <summary>The document does not exist.</summary>
    NotFound,

    /// <summary>Saving the status was cancelled by another rule.</summary>
    Cancelled,
}

/// <summary>Stable error codes of the submission guard.</summary>
public static class SubmissionErrorCodes
{
    /// <summary>Required fields are empty.</summary>
    public const string MissingRequiredFields = "submission_missing_required_fields";
}

/// <summary>
/// Default <see cref="ISubmissionGuard"/>: the union of every registered <see cref="IRequiredFieldProvider"/>.
/// A property counts as filled when its value is not empty, whitespace, <c>[]</c>, <c>{}</c> or <c>null</c>; for a
/// property that varies by culture every available culture must be filled. Required aliases the document type does not
/// have are ignored.
/// </summary>
public sealed class SubmissionGuard(
    IEnumerable<IRequiredFieldProvider> providers,
    IContentService contentService,
    IOptionsMonitor<SubmissionOptions> options,
    IEnumerable<IFieldLabelProvider>? labelProviders = null) : ISubmissionGuard
{
    private readonly IFieldLabelProvider[] _labelProviders = labelProviders?.ToArray() ?? [];

    /// <inheritdoc />
    public IReadOnlyList<MissingField> Check(IContent content, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        culture ??= CultureInfo.GetCultureInfo("en");

        var missing = new List<MissingField>();
        var required = Distinct(providers.SelectMany(p => p.GetRequiredFields(content)));
        foreach (var field in required)
        {
            var propertyType = content.Properties.FirstOrDefault(p => p.Alias == field.Alias)?.PropertyType;
            if (propertyType is null)
            {
                continue;
            }

            var labels = LabelsOf(field, propertyType);
            var label = Pick(labels, culture) ?? FallbackLabel(propertyType.Name, field.Alias);
            if (propertyType.VariesByCulture())
            {
                foreach (var c in content.AvailableCultures.Order(StringComparer.Ordinal))
                {
                    if (IsEmpty(content.GetValue(field.Alias, c)))
                    {
                        missing.Add(new MissingField(field.Alias, label, c, labels));
                    }
                }
            }
            else if (IsEmpty(content.GetValue(field.Alias)))
            {
                missing.Add(new MissingField(field.Alias, label, null, labels));
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public IReadOnlyList<RequiredField> RequiredFieldsFor(string documentTypeAlias)
    {
        ArgumentNullException.ThrowIfNull(documentTypeAlias);
        return Distinct(providers.SelectMany(p => p.GetRequiredFields(documentTypeAlias))).ToList();
    }

    private static IEnumerable<RequiredField> Distinct(IEnumerable<RequiredField> fields) => fields
        .GroupBy(f => f.Alias, StringComparer.Ordinal)
        .Select(g => g.FirstOrDefault(f => f.Labels is not null) ?? g.First());

    /// <inheritdoc />
    public bool IsSubmitted(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = options.CurrentValue;
        return !string.IsNullOrWhiteSpace(settings.StatusProperty)
            && content.HasProperty(settings.StatusProperty)
            && Codes(content.GetValue(settings.StatusProperty)).Any(code => settings.EffectiveSubmittedValues.Contains(code, StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public SubmissionResult Submit(Guid documentKey, CultureInfo? culture = null, int userId = -1)
    {
        var content = contentService.GetById(documentKey);
        if (content is null)
        {
            return new SubmissionResult(SubmissionStatus.NotFound, []);
        }

        var missing = Check(content, culture);
        if (missing.Count > 0)
        {
            return new SubmissionResult(SubmissionStatus.MissingRequiredFields, missing);
        }

        var settings = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.StatusProperty) || !content.HasProperty(settings.StatusProperty) || IsSubmitted(content))
        {
            return new SubmissionResult(SubmissionStatus.Submitted, []);
        }

        var property = content.Properties[settings.StatusProperty]!;
        var stored = property.GetValue();
        // Dropdowns store a JSON array of values; text properties a plain string.
        object value = stored is string s && s.TrimStart().StartsWith('[') || property.PropertyType.PropertyEditorAlias == Constants.PropertyEditors.Aliases.DropDownListFlexible
            ? JsonSerializer.Serialize(new[] { settings.SubmitValue })
            : settings.SubmitValue;
        content.SetValue(settings.StatusProperty, value);

        var result = contentService.Save(content, userId);
        return result.Success
            ? new SubmissionResult(SubmissionStatus.Submitted, [])
            : new SubmissionResult(SubmissionStatus.Cancelled, [], string.Join(" ", result.EventMessages?.GetAll().Select(m => m.Message) ?? []));
    }

    /// <summary>The values of a status property: a JSON array of strings (dropdown) or a plain string.</summary>
    public static IReadOnlyList<string> Codes(object? value)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        text = text.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(text) ?? [];
            }
            catch (JsonException)
            {
                return [text];
            }
        }

        return [text];
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s) || s.Trim() is "[]" or "{}" or "null" or "\"\"",
        _ => false,
    };

    private Dictionary<string, string> LabelsOf(RequiredField field, IPropertyType propertyType)
    {
        var known = field.Labels is { Count: > 0 }
            ? field.Labels
            : _labelProviders.Select(p => p.GetLabels(field.Alias)).FirstOrDefault(l => l is { Count: > 0 });
        var labels = (known ?? new Dictionary<string, string>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Value) && !IsLocalizationKey(l.Value))
            .ToDictionary(l => l.Key.Trim().ToLowerInvariant(), l => l.Value.Trim(), StringComparer.Ordinal);

        // nl and en are always present, so a consumer never has to guess; the fallback is the property name, else the alias.
        var fallback = FallbackLabel(propertyType.Name, field.Alias);
        foreach (var language in new[] { "nl", "en" })
        {
            labels.TryAdd(language, fallback);
        }

        return labels;
    }

    /// <summary>The property name, or the alias when the name is empty or a backoffice localization key (<c>#lsObjectModel_license</c>).</summary>
    private static string FallbackLabel(string? propertyName, string alias) =>
        string.IsNullOrWhiteSpace(propertyName) || IsLocalizationKey(propertyName) ? alias : propertyName;

    private static bool IsLocalizationKey(string text) => text.TrimStart().StartsWith('#');

    private static string? Pick(IReadOnlyDictionary<string, string> labels, CultureInfo culture) =>
        labels.TryGetValue(culture.TwoLetterISOLanguageName, out var label) ? label
        : labels.TryGetValue("en", out var en) ? en
        : labels.Values.FirstOrDefault();
}

/// <summary>
/// Enforces the submission guard in the content layer: a save that moves a document into a submitted status
/// (<see cref="SubmissionOptions.StatusProperty"/>) is cancelled while required fields are empty, through every route
/// (backoffice, Management API, integrations, code). Drafts can always be saved. Inactive when no status property is set.
/// </summary>
public sealed class SubmissionGuardHandler(
    ISubmissionGuard guard,
    IContentService contentService,
    IOptionsMonitor<SubmissionOptions> options) : INotificationHandler<ContentSavingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentSavingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.IsNullOrWhiteSpace(options.CurrentValue.StatusProperty))
        {
            return;
        }

        foreach (var content in notification.SavedEntities.Where(c => !c.Blueprint && guard.IsSubmitted(c)))
        {
            // Only the move into a submitted status is checked; saving an item that is already submitted is not.
            if (content.HasIdentity && contentService.GetById(content.Id) is { } stored && guard.IsSubmitted(stored))
            {
                continue;
            }

            var missing = guard.Check(content, CultureInfo.CurrentUICulture);
            if (missing.Count > 0)
            {
                notification.CancelOperation(new EventMessage(
                    SubmissionErrorCodes.MissingRequiredFields,
                    SubmissionMessages.MissingFields(missing, CultureInfo.CurrentUICulture),
                    EventMessageType.Error));
                return;
            }
        }
    }
}

/// <summary>Messages of the submission guard, in Dutch and English.</summary>
public static class SubmissionMessages
{
    /// <summary>The message listing the empty required fields.</summary>
    public static string MissingFields(IReadOnlyList<MissingField> missing, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(culture);
        var list = string.Join(", ", missing.Select(m => m.Culture is null ? m.Label : $"{m.Label} ({m.Culture})"));
        return culture.TwoLetterISOLanguageName == "nl"
            ? $"Inzenden niet mogelijk: verplichte velden zijn leeg: {list}."
            : $"Submission not possible: required fields are empty: {list}.";
    }
}
