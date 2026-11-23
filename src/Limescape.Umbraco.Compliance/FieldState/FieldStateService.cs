using System.Globalization;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.FieldState;

/// <summary>Stable error codes of the field state rules.</summary>
public static class FieldStateErrorCodes
{
    /// <summary>A new or changed value for an archived field.</summary>
    public const string FieldArchived = "field_archived";

    /// <summary>A deprecated code that the document did not hold already.</summary>
    public const string ValueDeprecated = "value_deprecated";
}

/// <summary>A save that the field state rules refuse.</summary>
/// <param name="Code"><see cref="FieldStateErrorCodes.FieldArchived"/> or <see cref="FieldStateErrorCodes.ValueDeprecated"/>.</param>
/// <param name="Alias">Property propertyAlias.</param>
/// <param name="Value">The refused value or code.</param>
public sealed record FieldStateViolation(string Code, string Alias, string Value);

/// <summary>
/// Archived fields and deprecated codes. A field is archived when it sits in the archived property group
/// of its document type (<see cref="FieldStateOptions.ArchivedGroup"/>) or is flagged in the <see cref="IFieldStateStore"/>;
/// a code is deprecated when the store says so. Both only stop <em>new</em> values: a document keeps what it has.
/// </summary>
public interface IFieldStateService
{
    /// <summary>The stored state per property propertyAlias, to pass to <see cref="StateOf"/> when several fields are looked at.</summary>
    IReadOnlyDictionary<string, FieldStateInfo> Stored();

    /// <summary>Effective state of property <paramref name="propertyAlias"/> on <paramref name="type"/>: archived by group or by flag, plus the deprecated codes.</summary>
    FieldStateInfo StateOf(IContentType type, string propertyAlias, IReadOnlyDictionary<string, FieldStateInfo>? stored = null);

    /// <summary>
    /// The first refused change from the stored document <paramref name="before"/> (called at most once, and only when a field is archived or has deprecated codes; may return null for a new document) to <paramref name="content"/>, or
    /// null. A new non-empty value in an archived field is refused; a deprecated code is refused unless the stored document
    /// held it already. Unchanged values and clearing a value are never refused.
    /// </summary>
    FieldStateViolation? Check(IContent content, Func<IContent?> before);

    /// <summary>
    /// The refusal of changing property <paramref name="propertyAlias"/> of a document of <paramref name="type"/> from
    /// <paramref name="was"/> to <paramref name="now"/> (stored forms; null or empty for no value), or null: the rule of
    /// <see cref="Check"/> for one value, for callers that know the old and new value themselves.
    /// </summary>
    FieldStateViolation? CheckChange(IContentType type, string propertyAlias, string? was, string? now, IReadOnlyDictionary<string, FieldStateInfo>? stored = null);

    /// <summary>Stores the state of <paramref name="propertyAlias"/> and audits the change (old and new state, attributed to the acting user).</summary>
    void Apply(string propertyAlias, FieldStateInfo state);
}

/// <inheritdoc />
public sealed class FieldStateService(
    IFieldStateStore store,
    IContentTypeService contentTypeService,
    IAuditWriter auditWriter,
    IOptionsMonitor<FieldStateOptions> options) : IFieldStateService
{
    /// <summary>Entity type of the audit entry of a field state change.</summary>
    public const string AuditEntityType = "metadataField";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, FieldStateInfo> Stored() => store.GetAll();

    /// <inheritdoc />
    public FieldStateInfo StateOf(IContentType type, string propertyAlias, IReadOnlyDictionary<string, FieldStateInfo>? stored = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        stored ??= store.GetAll();
        var kept = stored.GetValueOrDefault(propertyAlias) ?? FieldStateInfo.None;
        return kept.Archived || !InArchivedGroup(type, propertyAlias) ? kept : kept with { Archived = true };
    }

    /// <inheritdoc />
    public FieldStateViolation? Check(IContent content, Func<IContent?> before)
    {
        ArgumentNullException.ThrowIfNull(content);
        var type = contentTypeService.Get(content.ContentType.Key);
        if (type is null)
        {
            return null;
        }

        var states = store.GetAll();
        var storedDocument = new Lazy<IContent?>(before);
        foreach (var property in content.Properties)
        {
            var state = StateOf(type, property.Alias, states);
            if (!state.Archived && state.DeprecatedValues.Count == 0)
            {
                continue;
            }

            foreach (var value in property.Values)
            {
                var was = storedDocument.Value?.GetValue(property.Alias, value.Culture, value.Segment);
                if (Refusal(state, property.Alias, Text(was), Text(value.EditedValue)) is { } violation)
                {
                    return violation;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public FieldStateViolation? CheckChange(IContentType type, string propertyAlias, string? was, string? now, IReadOnlyDictionary<string, FieldStateInfo>? stored = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Refusal(StateOf(type, propertyAlias, stored), propertyAlias, Text(was), Text(now));
    }

    private static FieldStateViolation? Refusal(FieldStateInfo state, string propertyAlias, string? was, string? now)
    {
        if (now is null || string.Equals(now, was, StringComparison.Ordinal))
        {
            return null;
        }

        if (state.Archived)
        {
            return new FieldStateViolation(FieldStateErrorCodes.FieldArchived, propertyAlias, now);
        }

        if (state.DeprecatedValues.Count == 0)
        {
            return null;
        }

        var deprecated = new HashSet<string>(state.DeprecatedValues, StringComparer.OrdinalIgnoreCase);
        var held = Codes(was);
        return Codes(now).FirstOrDefault(code => deprecated.Contains(code) && !held.Contains(code, StringComparer.OrdinalIgnoreCase)) is { } refused
            ? new FieldStateViolation(FieldStateErrorCodes.ValueDeprecated, propertyAlias, refused)
            : null;
    }

    /// <inheritdoc />
    public void Apply(string propertyAlias, FieldStateInfo state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyAlias);
        ArgumentNullException.ThrowIfNull(state);

        var before = store.GetState(propertyAlias);
        store.SetState(propertyAlias, state);
        auditWriter.Write(new AuditRecord(AuditActions.FieldStateChange, AuditEntityType, null)
        {
            Field = propertyAlias,
            OldValue = Describe(before),
            NewValue = Describe(state),
        });
    }

    private static string Describe(FieldStateInfo state) =>
        JsonSerializer.Serialize(new { archived = state.Archived, deprecatedValues = state.DeprecatedValues.Order(StringComparer.Ordinal) }, Json);

    private bool InArchivedGroup(IContentType type, string propertyAlias)
    {
        var name = options.CurrentValue.ArchivedGroup?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return type.CompositionPropertyGroups.Any(group =>
            (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(group.Alias, name, StringComparison.OrdinalIgnoreCase)
                || (group.Alias?.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) ?? false))
            && (group.PropertyTypes?.Any(p => string.Equals(p.Alias, propertyAlias, StringComparison.Ordinal)) ?? false));
    }

    private static string? Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) is { } text && !string.IsNullOrWhiteSpace(text) && text.Trim() is not ("[]" or "null")
            ? text.Trim()
            : null;

    /// <summary>The codes in a stored value: a JSON array of strings (list fields) or the value itself.</summary>
    private static List<string> Codes(string? stored)
    {
        if (stored is null)
        {
            return [];
        }

        if (stored.StartsWith('['))
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(stored) ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            }
            catch (JsonException)
            {
                // Not a list of codes after all: treat the value as one code.
            }
        }

        return [stored];
    }
}
