using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Infrastructure;

/// <summary>
/// Shared setup for the submission guard tests: a document type with title, licence, summary and a status property,
/// configured in <see cref="UmbracoTestSite"/> (title and licence required by configuration) and a schema snapshot
/// file that requires summary for this type.
/// </summary>
internal static class SubmissionTestBed
{
    public const string DocumentType = "lsSubmissionTest";

    public const string StatusProperty = "lsSubmissionStatus";

    /// <summary>Snapshot path, relative to the content root.</summary>
    public const string SnapshotFile = "schema-snapshot.json";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static IContentType? _type;

    /// <summary>The snapshot: summary is required for type "submissionTest" (matches lsSubmissionTest), source only at the reviewer step.</summary>
    public const string Snapshot = """
        {
          "version": 3,
          "fields": [
            { "alias": "summary", "label": { "nl": "Samenvatting", "en": "Summary" }, "levels": [ { "scope": "type:submissionTest", "required": true } ] },
            { "alias": "licence", "label": { "nl": "Licentie", "en": "Licence" }, "levels": [ { "scope": "all", "required": true } ] },
            { "alias": "title", "label": { "nl": "Titel", "en": "Title" }, "levels": [ { "scope": "step:reviewer", "required": true } ] }
          ]
        }
        """;

    public static async Task<IContentType> TypeAsync(UmbracoTestSite site)
    {
        await Lock.WaitAsync();
        try
        {
            if (_type is not null)
            {
                return _type;
            }

            var contentRoot = site.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ContentRootPath;
            await System.IO.File.WriteAllTextAsync(Path.Combine(contentRoot, SnapshotFile), Snapshot);

            var shortStringHelper = site.Services.GetRequiredService<IShortStringHelper>();
            var textstring = await site.Services.GetRequiredService<IDataTypeService>().GetAsync(Constants.DataTypes.Guids.TextstringGuid)
                ?? throw new InvalidOperationException("Textstring data type not found");
            var type = new ContentType(shortStringHelper, Constants.System.Root) { Alias = DocumentType, Name = "Submission test", AllowedAsRoot = true };
            type.AddPropertyGroup("content", "Content");
            foreach (var alias in new[] { "title", "licence", "summary", StatusProperty })
            {
                type.AddPropertyType(new PropertyType(shortStringHelper, textstring, alias) { Name = char.ToUpperInvariant(alias[0]) + alias[1..] }, "content", "Content");
            }

            var created = await site.Services.GetRequiredService<IContentTypeService>().CreateAsync(type, Constants.Security.SuperUserKey);
            Assert.True(created.Success, $"creating submission test type failed: {created.Result}");
            _type = type;
            return type;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Creates and saves a draft with the given values.</summary>
    public static async Task<IContent> DraftAsync(UmbracoTestSite site, params (string Alias, string? Value)[] values)
    {
        var type = await TypeAsync(site);
        var contentService = site.Services.GetRequiredService<IContentService>();
        var content = contentService.Create($"Submission {Guid.NewGuid():N}", Constants.System.Root, type);
        foreach (var (alias, value) in values)
        {
            content.SetValue(alias, value);
        }

        Assert.True(contentService.Save(content).Success);
        return content;
    }
}
