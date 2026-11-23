using System.Globalization;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Computes the fingerprint of a document's draft: what a reviewer approves and what the gate compares at publish time.
/// </summary>
public interface IContentVersionHasher
{
    /// <summary>Lowercase hex SHA-256 over the document type, template, names and edited property values of every culture and segment.</summary>
    string ComputeHash(IContent content);
}

/// <summary>
/// Default <see cref="IContentVersionHasher"/>. Umbraco keeps the same version id while a draft is saved again, so the
/// version id alone cannot tell whether the content changed after approval; this hash can.
/// </summary>
public sealed class ContentVersionHasher(IOptionsMonitor<PublicationGateOptions> options) : IContentVersionHasher
{
    /// <summary>Length of a hash in characters.</summary>
    public const int HashLength = 64;

    /// <inheritdoc />
    public string ComputeHash(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", content.ContentType.Alias);
            if (content.TemplateId is { } templateId)
            {
                json.WriteNumber("template", templateId);
            }
            else
            {
                json.WriteNull("template");
            }

            json.WriteString("name", content.Name);

            json.WriteStartObject("names");
            foreach (var culture in (content.CultureInfos?.Values ?? []).OrderBy(c => c.Culture, StringComparer.OrdinalIgnoreCase))
            {
                json.WriteString(culture.Culture.ToLowerInvariant(), culture.Name);
            }

            json.WriteEndObject();

            json.WriteStartArray("properties");
            foreach (var property in content.Properties.Where(p => !options.CurrentValue.ExcludedPropertyAliases.Contains(p.Alias, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.Alias, StringComparer.Ordinal))
            {
                var values = property.Values
                    .OrderBy(v => v.Culture ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(v => v.Segment ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                {
                    if (value.EditedValue is null or "")
                    {
                        // An empty, a null and an absent value are the same draft.
                        continue;
                    }

                    json.WriteStartArray();
                    json.WriteStringValue(property.Alias);
                    json.WriteStringValue(value.Culture?.ToLowerInvariant());
                    json.WriteStringValue(value.Segment?.ToLowerInvariant());
                    json.WriteStringValue(Normalize(value.EditedValue));
                    json.WriteEndArray();
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>
    /// The same stored value can arrive as a different CLR type depending on whether the document was loaded from the
    /// database or just set by a property editor (int or long, 2.5 or 2.50). Normalise to an invariant string.
    /// </summary>
    private static string Normalize(object value) => value switch
    {
        string s => s,
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
        // Dividing by 1.000… drops trailing zeros (2.50m becomes 2.5m).
        decimal m => (m / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        // A toggle is stored as an integer and set as a boolean.
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}
