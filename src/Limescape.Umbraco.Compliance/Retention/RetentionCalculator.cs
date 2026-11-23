using System.Text.Json;

namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// The facts about one item that retention depends on. All dates are UTC.
/// </summary>
/// <param name="Key">Item key.</param>
/// <param name="ContentTypeKey">Document or media type key.</param>
/// <param name="LastModified">Latest modification.</param>
public sealed record RetentionSubject(Guid Key, Guid ContentTypeKey, DateTime LastModified)
{
    /// <summary>Latest publication; for media the creation date. Null when never published.</summary>
    public DateTime? Published { get; init; }

    /// <summary>Archive date; null when not archived.</summary>
    public DateTime? Archived { get; init; }

    /// <summary>Raw property values by alias, used for property-value assignments and date-property start events.</summary>
    public Func<string, IEnumerable<object?>> PropertyValues { get; init; } = _ => [];
}

/// <summary>
/// How one class applies to an item.
/// </summary>
/// <param name="Class">The class.</param>
/// <param name="StartDate">Start of the period; null while the start event has not happened.</param>
/// <param name="Until">End of the period; null while the start event has not happened.</param>
/// <param name="Source">The assignment target that made the class apply.</param>
public sealed record RetentionRule(RetentionClass Class, DateTime? StartDate, DateTime? Until, RetentionAssignmentTarget Source)
{
    /// <summary>True when the start event has not happened, so the period is open-ended.</summary>
    public bool IsPending => Until is null;
}

/// <summary>
/// Outcome of a calculation: every applicable rule, and the governing (longest) one.
/// </summary>
/// <param name="Rules">Every class that applies, longest first.</param>
public sealed record RetentionResult(IReadOnlyList<RetentionRule> Rules)
{
    /// <summary>No class applies.</summary>
    public static RetentionResult None { get; } = new([]);

    /// <summary>The rule with the longest period; a pending rule is longer than any dated one.</summary>
    public RetentionRule? Governing => Rules.Count == 0 ? null : Rules[0];

    /// <summary>True when the longest period is open-ended because a start event has not happened.</summary>
    public bool IsOpenEnded => Governing?.IsPending == true;

    /// <summary>End of the longest period; null when no class applies or the period is open-ended.</summary>
    public DateTime? Until => Governing?.Until;

    /// <summary>Status at <paramref name="nowUtc"/>.</summary>
    public RetentionStatus StatusAt(DateTime nowUtc) => RetentionCalculator.Status(Rules.Count > 0, IsOpenEnded, Until, nowUtc);
}

/// <summary>
/// Pure retention rules: which classes apply to an item, when each period ends, and how a new
/// result is merged with the stored one. No I/O, so every rule is unit-tested.
/// </summary>
public static class RetentionCalculator
{
    /// <summary>
    /// The classes that apply to <paramref name="subject"/>. Item assignments override all others; otherwise every
    /// matching type and property-value assignment applies. A class assigned twice counts once.
    /// </summary>
    public static IReadOnlyList<(RetentionClass Class, RetentionAssignmentTarget Source)> Applicable(
        RetentionSubject subject,
        IEnumerable<RetentionAssignment> assignments,
        IReadOnlyDictionary<Guid, RetentionClass> classes)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(classes);

        var all = assignments.Where(a => classes.ContainsKey(a.ClassKey)).ToList();
        var itemAssignments = all.Where(a => a.Target == RetentionAssignmentTarget.Item && a.ItemKey == subject.Key).ToList();
        var matching = itemAssignments.Count > 0
            ? itemAssignments
            : all.Where(a => a.Target switch
            {
                RetentionAssignmentTarget.ContentType => a.ContentTypeKey == subject.ContentTypeKey,
                RetentionAssignmentTarget.PropertyValue => (a.ContentTypeKey is null || a.ContentTypeKey == subject.ContentTypeKey)
                    && a.PropertyAlias is not null
                    && a.PropertyValue is not null
                    && HasValue(subject.PropertyValues(a.PropertyAlias), a.PropertyValue),
                _ => false,
            }).ToList();

        return matching
            .GroupBy(a => a.ClassKey)
            .Select(g => (classes[g.Key], g.Min(a => a.Target)))
            .ToList();
    }

    /// <summary>
    /// Calculates every applicable rule. The longest period wins: rules are ordered longest first, and a
    /// rule whose start event has not happened yet is open-ended, so it is longer than any dated rule.
    /// </summary>
    public static RetentionResult Calculate(RetentionSubject subject, IEnumerable<(RetentionClass Class, RetentionAssignmentTarget Source)> applicable)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(applicable);

        var rules = applicable
            .Select(a =>
            {
                var start = StartDate(subject, a.Class);
                return new RetentionRule(a.Class, start, start is { } s ? a.Class.Duration.AddTo(s) : null, a.Source);
            })
            .OrderByDescending(r => r.Until ?? DateTime.MaxValue)
            .ThenBy(r => r.Class.Name, StringComparer.Ordinal)
            .ToList();

        return rules.Count == 0 ? RetentionResult.None : new RetentionResult(rules);
    }

    /// <summary>The start date of <paramref name="retentionClass"/> for <paramref name="subject"/>, or null if the start event has not happened.</summary>
    public static DateTime? StartDate(RetentionSubject subject, RetentionClass retentionClass)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(retentionClass);

        return retentionClass.StartEvent switch
        {
            RetentionStartEvent.Archived => subject.Archived,
            RetentionStartEvent.Published => subject.Published,
            RetentionStartEvent.LastModified => subject.LastModified,
            RetentionStartEvent.DateProperty => retentionClass.StartPropertyAlias is { } alias
                ? subject.PropertyValues(alias).Select(ParseDate).FirstOrDefault(d => d is not null)
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// Merges a new calculation with the stored period. A stored period never shortens unless an administrator has
    /// confirmed the shortening; open-ended is longer than any date.
    /// </summary>
    /// <returns>The period to store, and whether a shorter calculated period was refused.</returns>
    public static (bool OpenEnded, DateTime? Until, bool ShorteningRefused) Merge(
        bool storedOpenEnded,
        DateTime? storedUntil,
        RetentionResult calculated,
        bool allowShortening)
    {
        ArgumentNullException.ThrowIfNull(calculated);

        if (allowShortening || !IsShorter(calculated.IsOpenEnded, calculated.Until, storedOpenEnded, storedUntil))
        {
            return (calculated.IsOpenEnded, calculated.Until, false);
        }

        return (storedOpenEnded, storedUntil, true);
    }

    /// <summary>
    /// True when replacing period b by period a would end retention earlier. No period (no class) is the shortest.
    /// An open-ended period (start event not happened yet) that becomes dated is not a shortening: the start event
    /// has happened, which is how a period is meant to start. Dropping an open-ended period altogether is.
    /// </summary>
    public static bool IsShorter(bool aOpenEnded, DateTime? aUntil, bool bOpenEnded, DateTime? bUntil)
    {
        if (aOpenEnded || (!bOpenEnded && bUntil is null))
        {
            return false;
        }

        if (bOpenEnded)
        {
            return aUntil is null;
        }

        return aUntil is null || aUntil < bUntil;
    }

    /// <summary>Status of a stored or calculated period.</summary>
    public static RetentionStatus Status(bool hasClasses, bool openEnded, DateTime? until, DateTime nowUtc)
    {
        if (openEnded)
        {
            return RetentionStatus.Pending;
        }

        if (until is { } u)
        {
            return u > nowUtc ? RetentionStatus.Active : RetentionStatus.Ended;
        }

        return hasClasses ? RetentionStatus.Pending : RetentionStatus.None;
    }

    /// <summary>
    /// True when one of the raw values equals <paramref name="expected"/> (case-insensitive). A value that is a JSON
    /// array (tags, multiple choice) or a comma-separated list matches when one of its elements does.
    /// </summary>
    public static bool HasValue(IEnumerable<object?> values, string expected)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var value in values)
        {
            foreach (var element in Elements(value))
            {
                if (string.Equals(element, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Parses a stored date value (DateTime, DateTimeOffset, ISO string, or a JSON object with a <c>date</c> member) to UTC.</summary>
    public static DateTime? ParseDate(object? value)
    {
        switch (value)
        {
            case DateTime dt:
                return dt == default ? null : AsUtc(dt);
            case DateTimeOffset dto:
                return dto.UtcDateTime;
            case string s when !string.IsNullOrWhiteSpace(s):
                var text = s.Trim();
                if (text.StartsWith('{'))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(text);
                        return json.RootElement.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String
                            ? ParseDate(date.GetString())
                            : null;
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }

                if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return parsed.UtcDateTime;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Umbraco 17 stores system dates in UTC; a date without kind is taken as UTC.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static IEnumerable<string> Elements(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                var text = s.Trim();
                if (text.StartsWith('['))
                {
                    List<string> items = [];
                    try
                    {
                        using var json = JsonDocument.Parse(text);
                        items.AddRange(json.RootElement.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!.Trim()));
                    }
                    catch (JsonException)
                    {
                        items.Add(text);
                    }

                    foreach (var item in items)
                    {
                        yield return item;
                    }

                    yield break;
                }

                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return part;
                }

                yield break;
            case IEnumerable<string> list:
                foreach (var item in list)
                {
                    yield return item.Trim();
                }

                yield break;
            default:
                yield return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                yield break;
        }
    }
}
