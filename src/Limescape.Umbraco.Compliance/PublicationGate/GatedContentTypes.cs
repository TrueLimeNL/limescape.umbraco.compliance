using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Decides whether a document falls under the publication gate.
/// </summary>
public interface IGatedContentTypes
{
    /// <summary>
    /// True when the document's type, or one of the types it is composed of, is listed in
    /// <see cref="PublicationGateOptions.GatedDocumentTypes"/>. Listing a composition gates every type built on it,
    /// including types created later.
    /// </summary>
    bool IsGated(IContent content);
}

/// <summary>
/// Default <see cref="IGatedContentTypes"/>. Composition aliases come from Umbraco's cached content types.
/// </summary>
public sealed class GatedContentTypes(IOptionsMonitor<PublicationGateOptions> options, IContentTypeService contentTypeService)
    : IGatedContentTypes
{
    /// <inheritdoc />
    public bool IsGated(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var gate = options.CurrentValue;
        if (gate.GatedDocumentTypes.Count == 0)
        {
            return false;
        }

        if (gate.IsGated(content.ContentType.Alias))
        {
            return true;
        }

        var compositions = contentTypeService.Get(content.ContentTypeId)?.CompositionAliases() ?? [];
        return compositions.Any(gate.IsGated);
    }
}
