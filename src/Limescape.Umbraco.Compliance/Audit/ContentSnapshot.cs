using System.Globalization;
using System.Text.Json;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// The audited values of a document or media item at one moment: the name per culture and every property value per
/// culture and segment, as strings. Two snapshots give the field-level diff of a save.
/// </summary>
public sealed class ContentSnapshot
{
    /// <summary>Field name used for the item's name, so it cannot clash with a property alias.</summary>
    public const string NameField = "@name";

    private ContentSnapshot(IReadOnlyDictionary<string, string?> values) => Values = values;

    /// <summary>Field → value. Fields are <c>alias</c>, <c>alias[culture]</c>, <c>alias[culture|segment]</c> and <see cref="NameField"/>.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>An empty snapshot, the "before" of a new item.</summary>
    public static ContentSnapshot Empty { get; } = new(new Dictionary<string, string?>());

    /// <summary>A snapshot of arbitrary fields, for entities that are not documents or media (for example user groups).</summary>
    public static ContentSnapshot FromValues(IEnumerable<KeyValuePair<string, string?>> values) =>
        new(new Dictionary<string, string?>(values, StringComparer.Ordinal));

    /// <summary>Takes a snapshot of the edited (draft) values of <paramref name="item"/>.</summary>
    public static ContentSnapshot Of(IContentBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (item.CultureInfos is { Count: > 0 } cultureInfos)
        {
            foreach (var info in cultureInfos)
            {
                values[Field(NameField, info.Culture, null)] = info.Name;
            }
        }
        else
        {
            values[NameField] = item.Name;
        }

        foreach (var property in item.Properties)
        {
            foreach (var value in property.Values)
            {
                values[Field(property.Alias, value.Culture, value.Segment)] = Format(value.EditedValue);
            }
        }

        return new ContentSnapshot(values);
    }

    /// <summary>
    /// Fields whose value differs between <paramref name="before"/> and <paramref name="after"/>, in field order.
    /// A field that is empty on both sides is not a change.
    /// </summary>
    public static IReadOnlyList<FieldChange> Diff(ContentSnapshot before, ContentSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<FieldChange>();
        foreach (var field in before.Values.Keys.Union(after.Values.Keys).Order(StringComparer.Ordinal))
        {
            var old = Normalize(before.Values.GetValueOrDefault(field));
            var @new = Normalize(after.Values.GetValueOrDefault(field));
            if (!string.Equals(old, @new, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, old, @new));
            }
        }

        return changes;
    }

    /// <summary>A stored value as text: strings as they are, numbers and dates invariant, other objects as JSON.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };

    private static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Field(string alias, string? culture, string? segment) =>
        string.IsNullOrEmpty(culture) && string.IsNullOrEmpty(segment)
            ? alias
            : string.IsNullOrEmpty(segment) ? $"{alias}[{culture}]" : $"{alias}[{culture}|{segment}]";
}

/// <summary>One changed field of a save.</summary>
/// <param name="Field">Property alias (with culture and segment when the property varies), or <see cref="ContentSnapshot.NameField"/>.</param>
/// <param name="OldValue">Value before the save; null when empty.</param>
/// <param name="NewValue">Value after the save; null when empty.</param>
public sealed record FieldChange(string Field, string? OldValue, string? NewValue);
