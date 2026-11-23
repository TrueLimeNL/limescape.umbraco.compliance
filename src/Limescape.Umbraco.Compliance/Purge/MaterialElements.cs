using System.Text.Json;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Purge;

/// <summary>
/// The elements (media items) of a document, read from the media picker property in <see cref="PurgeOptions.ElementsProperty"/>,
/// and whether other items still use them.
/// </summary>
public interface IMaterialElements
{
    /// <summary>Keys of the media items the document picked, in order; empty when the property is not configured, missing or empty.</summary>
    IReadOnlyList<Guid> KeysOf(IContent content);

    /// <summary>
    /// True when an item other than the ones in <paramref name="ownerIds"/> picks the media item (tracked reference).
    /// With <paramref name="ignoreTrashedUsers"/> an item in the recycle bin does not count.
    /// </summary>
    bool IsUsedElsewhere(IMedia media, IReadOnlySet<int> ownerIds, bool ignoreTrashedUsers);
}

/// <summary>
/// Default <see cref="IMaterialElements"/>: reads the stored picker value (the media picker stores a JSON array of
/// objects with a <c>mediaKey</c>; a comma-separated list of keys or media UDIs is understood too) and asks the
/// relation service which items reference a media item.
/// </summary>
public sealed class MaterialElements(IOptionsMonitor<PurgeOptions> options, IRelationService relationService, IEntityService entityService) : IMaterialElements
{
    /// <inheritdoc />
    public IReadOnlyList<Guid> KeysOf(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var alias = options.CurrentValue.ElementsProperty;
        if (string.IsNullOrWhiteSpace(alias) || !content.HasProperty(alias))
        {
            return [];
        }

        return Parse(content.GetValue(alias));
    }

    /// <inheritdoc />
    public bool IsUsedElsewhere(IMedia media, IReadOnlySet<int> ownerIds, bool ignoreTrashedUsers)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(ownerIds);
        foreach (var relation in relationService.GetByChildId(media.Id, Constants.Conventions.RelationTypes.RelatedMediaAlias))
        {
            if (ownerIds.Contains(relation.ParentId))
            {
                continue;
            }

            var user = entityService.Get(relation.ParentId);
            if (user is null || (ignoreTrashedUsers && user.Trashed))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static IReadOnlyList<Guid> Parse(object? value)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var keys = new List<Guid>();
        text = text.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    var raw = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("mediaKey", out var key) ? key.GetString() : item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (TryKey(raw, out var parsed))
                    {
                        keys.Add(parsed);
                    }
                }
            }
            catch (JsonException)
            {
                return [];
            }
        }
        else
        {
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryKey(part, out var parsed))
                {
                    keys.Add(parsed);
                }
            }
        }

        return [.. keys.Distinct()];
    }

    private static bool TryKey(string? raw, out Guid key)
    {
        key = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // A media UDI is umb://media/{guid without dashes}.
        return Guid.TryParse(raw.StartsWith("umb://media/", StringComparison.OrdinalIgnoreCase) ? raw["umb://media/".Length..] : raw, out key);
    }
}
