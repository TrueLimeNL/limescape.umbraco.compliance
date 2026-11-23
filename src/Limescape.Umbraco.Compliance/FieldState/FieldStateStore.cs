using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.FieldState;

/// <summary>
/// State of one metadata field beyond what the object model says: archived, and the codes of a list field that can no
/// longer be chosen (deprecated). Existing values are never touched by either.
/// </summary>
/// <param name="Archived">The field is archived: no new value can be written.</param>
/// <param name="DeprecatedValues">Codes that a document may keep but cannot newly receive.</param>
public sealed record FieldStateInfo(bool Archived, IReadOnlySet<string> DeprecatedValues)
{
    /// <summary>A field that is neither archived nor has deprecated codes.</summary>
    public static FieldStateInfo None { get; } = new(false, new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Per-tenant storage of <see cref="FieldStateInfo"/>, per property propertyAlias. Not a database table: nothing to migrate.
/// </summary>
public interface IFieldStateStore
{
    /// <summary>The stored state per property propertyAlias; aliases without stored state are left out.</summary>
    IReadOnlyDictionary<string, FieldStateInfo> GetAll();

    /// <summary>The stored state of one property propertyAlias; <see cref="FieldStateInfo.None"/> when there is none.</summary>
    FieldStateInfo GetState(string propertyAlias);

    /// <summary>Replaces the stored state of <paramref name="propertyAlias"/> by <paramref name="state"/>.</summary>
    void SetState(string propertyAlias, FieldStateInfo state);
}

/// <summary>
/// <see cref="IFieldStateStore"/> on Umbraco's key-value table (<see cref="IKeyValueService"/>), one row per flag
/// (<c>Limescape.FieldState:{propertyAlias}:archived</c>, <c>Limescape.FieldState:{propertyAlias}:deprecated:{code}</c>, value <c>1</c> or
/// <c>0</c>). One row per flag keeps every value short (the column is limited) and makes concurrent changes of different
/// codes independent. Umbraco has no delete, so a cleared flag stays as a <c>0</c> row.
/// </summary>
public sealed class KeyValueFieldStateStore(IKeyValueService keyValues) : IFieldStateStore
{
    private const string Prefix = "Limescape.FieldState:";
    private const string ArchivedSuffix = ":archived";
    private const string DeprecatedMarker = ":deprecated:";
    private const string On = "1";
    private const string Off = "0";

    /// <summary>Longest propertyAlias plus code that fits a key (Umbraco keys are at most 256 characters).</summary>
    public const int MaxKeyPayload = 200;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, FieldStateInfo> GetAll()
    {
        var archived = new HashSet<string>(StringComparer.Ordinal);
        var deprecated = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in keyValues.FindByKeyPrefix(Prefix) ?? new Dictionary<string, string?>())
        {
            if (value != On)
            {
                continue;
            }

            var rest = key[Prefix.Length..];
            if (rest.EndsWith(ArchivedSuffix, StringComparison.Ordinal))
            {
                archived.Add(rest[..^ArchivedSuffix.Length]);
            }
            else if (rest.IndexOf(DeprecatedMarker, StringComparison.Ordinal) is var at and > 0)
            {
                var propertyAlias = rest[..at];
                if (!deprecated.TryGetValue(propertyAlias, out var codes))
                {
                    deprecated[propertyAlias] = codes = new HashSet<string>(StringComparer.Ordinal);
                }

                codes.Add(rest[(at + DeprecatedMarker.Length)..]);
            }
        }

        return archived.Union(deprecated.Keys, StringComparer.Ordinal)
            .ToDictionary(propertyAlias => propertyAlias, propertyAlias => new FieldStateInfo(archived.Contains(propertyAlias), deprecated.GetValueOrDefault(propertyAlias) ?? FieldStateInfo.None.DeprecatedValues), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public FieldStateInfo GetState(string propertyAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyAlias);
        return GetAll().GetValueOrDefault(propertyAlias) ?? FieldStateInfo.None;
    }

    /// <inheritdoc />
    public void SetState(string propertyAlias, FieldStateInfo state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyAlias);
        ArgumentNullException.ThrowIfNull(state);

        var before = GetState(propertyAlias);
        keyValues.SetValue(Prefix + propertyAlias + ArchivedSuffix, state.Archived ? On : Off);
        foreach (var code in before.DeprecatedValues.Except(state.DeprecatedValues, StringComparer.Ordinal))
        {
            keyValues.SetValue(Prefix + propertyAlias + DeprecatedMarker + code, Off);
        }

        foreach (var code in state.DeprecatedValues)
        {
            keyValues.SetValue(Prefix + propertyAlias + DeprecatedMarker + code, On);
        }
    }
}
