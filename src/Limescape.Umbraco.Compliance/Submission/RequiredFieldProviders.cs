using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Limescape.Umbraco.Compliance.FieldState;
using Limescape.Umbraco.Compliance.PublicationGate;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Submission;

/// <summary>
/// Supplies the fields that must be filled before a document can be submitted for review. Register any number
/// of providers; the guard requires the union. The package ships one for configuration, one for the Umbraco <c>mandatory</c>
/// flag and one for a metadata schema snapshot; an integration can add its own (for example one that asks its schema service).
/// </summary>
public interface IRequiredFieldProvider
{
    /// <summary>Required fields for <paramref name="content"/> at submission.</summary>
    IEnumerable<RequiredField> GetRequiredFields(IContent content);

    /// <summary>
    /// Required fields for any document of the type <paramref name="documentTypeAlias"/>, for callers that describe a
    /// type without a document (a form, a metadata schema). Must agree with <see cref="GetRequiredFields(IContent)"/>.
    /// A provider that decides per document returns nothing here (the default).
    /// </summary>
    IEnumerable<RequiredField> GetRequiredFields(string documentTypeAlias) => [];
}

/// <summary>A field that must be filled.</summary>
/// <param name="Alias">Property alias.</param>
/// <param name="Labels">Label per language (for example nl, en), if the provider knows it.</param>
public sealed record RequiredField(string Alias, IReadOnlyDictionary<string, string>? Labels = null);

/// <summary>
/// Supplies the labels a person reads for a property alias (for example from a tenant profile). The submission guard asks
/// every registered provider when the required field itself has no labels; without one the label is the property name, or
/// the alias when that name is a backoffice localization key (<c>#...</c>), which never reaches a message.
/// </summary>
public interface IFieldLabelProvider
{
    /// <summary>Label per language (for example nl, en) of <paramref name="propertyAlias"/>, or <c>null</c> when this provider does not know it.</summary>
    IReadOnlyDictionary<string, string>? GetLabels(string propertyAlias);
}

/// <summary>
/// Required fields from configuration: <see cref="SubmissionOptions.RequiredFields"/>, per document type alias and
/// <c>*</c> for all types.
/// </summary>
public sealed class ConfiguredRequiredFieldProvider(IOptionsMonitor<SubmissionOptions> options) : IRequiredFieldProvider
{
    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return GetRequiredFields(content.ContentType.Alias);
    }

    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(string documentTypeAlias)
    {
        ArgumentNullException.ThrowIfNull(documentTypeAlias);
        var configured = options.CurrentValue.RequiredFields;
        foreach (var key in new[] { "*", documentTypeAlias })
        {
            if (configured.TryGetValue(key, out var aliases))
            {
                foreach (var alias in aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    yield return new RequiredField(alias.Trim());
                }
            }
        }
    }
}

/// <summary>
/// Required fields from Umbraco itself: every property of the document type with <c>mandatory</c> set (what a functional
/// administrator ticks). The backoffice already refuses to publish without it; this makes the
/// submission rule agree, on every route. System fields never count, because the host fills them: the status property
/// and <see cref="PublicationGateOptions.ExcludedPropertyAliases"/> (status, owners, archivedAt).
/// </summary>
public sealed class MandatoryPropertyRequiredFieldProvider(
    IContentTypeService contentTypeService,
    IOptionsMonitor<SubmissionOptions> options,
    IOptionsMonitor<PublicationGateOptions> gateOptions,
    IFieldStateService fieldStates) : IRequiredFieldProvider
{
    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var type = contentTypeService.Get(content.ContentType.Key);
        var stored = fieldStates.Stored();
        return content.Properties
            .Where(p => p.PropertyType.Mandatory && !IsSystemField(p.Alias) && !IsArchived(type, p.Alias, stored))
            .Select(p => new RequiredField(p.Alias))
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(string documentTypeAlias)
    {
        ArgumentNullException.ThrowIfNull(documentTypeAlias);
        var type = contentTypeService.Get(documentTypeAlias);
        var stored = fieldStates.Stored();
        return (type?.CompositionPropertyTypes ?? [])
            .Where(p => p.Mandatory && !IsSystemField(p.Alias) && !IsArchived(type, p.Alias, stored))
            .Select(p => new RequiredField(p.Alias))
            .ToList();
    }

    // An archived field is out of every form, so it cannot be required to submit. The platform clears `mandatory` when it
    // archives; this holds for a field that was archived another way.
    private bool IsArchived(IContentType? type, string alias, IReadOnlyDictionary<string, FieldStateInfo> stored) =>
        type is not null && fieldStates.StateOf(type, alias, stored).Archived;

    private bool IsSystemField(string alias) =>
        string.Equals(alias, options.CurrentValue.StatusProperty, StringComparison.OrdinalIgnoreCase)
        || gateOptions.CurrentValue.ExcludedPropertyAliases.Contains(alias, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Required fields from a metadata schema snapshot (JSON file at <see cref="SubmissionOptions.SchemaSnapshotPath"/>):
/// <c>{ "fields": [ { "alias", "label": { "nl", "en" }, "levels": [ { "scope", "required" } ] } ] }</c>.
/// A field is required at submission when a level with <c>required: true</c> has scope <c>all</c> or
/// <c>type:{alias}</c>, where alias is the document type alias, with or without the <c>ls</c> prefix
/// (<c>type:video</c> matches <c>lsVideo</c>). Other scopes (element, category, step:reviewer) do not apply to
/// submission of the document. The file is re-read when it changes; a missing or invalid file requires nothing and is logged.
/// </summary>
public sealed partial class SchemaSnapshotRequiredFieldProvider(
    IOptionsMonitor<SubmissionOptions> options,
    IHostEnvironment hostEnvironment,
    ILogger<SchemaSnapshotRequiredFieldProvider> logger) : IRequiredFieldProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private (string Path, DateTime Written, SnapshotDocument Snapshot)? _cache;

    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return GetRequiredFields(content.ContentType.Alias);
    }

    /// <inheritdoc />
    public IEnumerable<RequiredField> GetRequiredFields(string documentTypeAlias)
    {
        ArgumentNullException.ThrowIfNull(documentTypeAlias);
        if (Load() is not { } snapshot)
        {
            return [];
        }

        var typeAlias = documentTypeAlias;
        return (snapshot.Fields ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Alias) && !string.Equals(f.Status, "archived", StringComparison.OrdinalIgnoreCase))
            .Where(f => (f.Levels ?? []).Any(l => l.Required && Applies(l.Scope, typeAlias)))
            .Select(f => new RequiredField(f.Alias!, f.Label))
            .ToList();
    }

    /// <summary>True when a level scope applies to the submission of a document of <paramref name="typeAlias"/>.</summary>
    public static bool Applies(string? scope, string typeAlias)
    {
        ArgumentNullException.ThrowIfNull(typeAlias);
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (scope is null || !scope.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var alias = scope[5..].Trim();
        return string.Equals(alias, typeAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals("ls" + alias, typeAlias, StringComparison.OrdinalIgnoreCase);
    }

    private SnapshotDocument? Load()
    {
        var configured = options.CurrentValue.SchemaSnapshotPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(hostEnvironment.ContentRootPath, configured);
        try
        {
            var written = System.IO.File.GetLastWriteTimeUtc(path);
            lock (_lock)
            {
                if (_cache is { } cached && cached.Path == path && cached.Written == written)
                {
                    return cached.Snapshot;
                }

                var snapshot = JsonSerializer.Deserialize<SnapshotDocument>(System.IO.File.ReadAllText(path), Json) ?? new SnapshotDocument();
                _cache = (path, written, snapshot);
                return snapshot;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogSnapshotUnreadable(logger, ex, path);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Submission guard: schema snapshot {Path} cannot be read; no snapshot fields are required")]
    private static partial void LogSnapshotUnreadable(ILogger logger, Exception exception, string path);

    private sealed class SnapshotDocument
    {
        public List<SnapshotField>? Fields { get; set; }
    }

    private sealed class SnapshotField
    {
        public string? Alias { get; set; }

        public Dictionary<string, string>? Label { get; set; }

        public string? Status { get; set; }

        public List<SnapshotLevel>? Levels { get; set; }
    }

    private sealed class SnapshotLevel
    {
        public string? Scope { get; set; }

        public bool Required { get; set; }
    }
}
