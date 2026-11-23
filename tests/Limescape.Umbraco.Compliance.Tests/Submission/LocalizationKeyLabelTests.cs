using System.Globalization;
using System.Net;
using Limescape.Umbraco.Compliance.Submission;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Submission;

/// <summary>
/// Under an object model the property name is a backoffice localization key (<c>#lsObjectModel_licence</c>). The
/// refusal to submit names the field with a profile label when a <see cref="IFieldLabelProvider"/> has one, and otherwise
/// with the alias; the key never reaches a message, on any route (negative test).
/// </summary>
public sealed class LocalizationKeyLabelTests(UmbracoTestSite site)
{
    private const string DocumentType = "lsKeyLabelTest";
    private const string Field = "keyedNote";
    private const string Key = "#lsObjectModel_keyedNote";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static IContentType? _type;

    private IContentService ContentService => site.Services.GetRequiredService<IContentService>();

    private async Task<IContentType> TypeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_type is not null)
            {
                return _type;
            }

            var strings = site.Services.GetRequiredService<IShortStringHelper>();
            var textstring = await site.Services.GetRequiredService<IDataTypeService>().GetAsync(Constants.DataTypes.Guids.TextstringGuid)
                ?? throw new InvalidOperationException("Textstring data type not found");
            var type = new ContentType(strings, Constants.System.Root) { Alias = DocumentType, Name = "Key label test", AllowedAsRoot = true };
            type.AddPropertyGroup("content", "Content");
            type.AddPropertyType(new PropertyType(strings, textstring, "title") { Name = "Title" }, "content", "Content");
            type.AddPropertyType(new PropertyType(strings, textstring, SubmissionTestBed.StatusProperty) { Name = "Status" }, "content", "Content");
            type.AddPropertyType(new PropertyType(strings, textstring, Field) { Name = Key, Mandatory = true }, "content", "Content");
            var created = await site.Services.GetRequiredService<IContentTypeService>().CreateAsync(type, Constants.Security.SuperUserKey);
            Assert.True(created.Success, $"creating key label test type failed: {created.Result}");
            _type = type;
            return type;
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<IContent> DraftAsync()
    {
        var content = ContentService.Create($"Key label {Guid.NewGuid():N}", Constants.System.Root, await TypeAsync());
        content.SetValue("title", "Wound care");
        Assert.True(ContentService.Save(content).Success);
        return content;
    }

    private SubmissionGuard Guard(params IFieldLabelProvider[] labelProviders) => new(
        site.Services.GetServices<IRequiredFieldProvider>(),
        ContentService,
        site.Services.GetRequiredService<IOptionsMonitor<SubmissionOptions>>(),
        labelProviders);

    private sealed class Profile : IFieldLabelProvider
    {
        public IReadOnlyDictionary<string, string>? GetLabels(string propertyAlias) =>
            propertyAlias == Field ? new Dictionary<string, string> { ["nl"] = "Notitie", ["en"] = "Note" } : null;
    }

    [Fact]
    public async Task Without_a_label_the_alias_is_named_and_the_localization_key_never_appears()
    {
        var draft = await DraftAsync();

        var missing = Guard().Check(draft, CultureInfo.GetCultureInfo("nl")).Single(m => m.Alias == Field);
        var text = SubmissionMessages.MissingFields([missing], CultureInfo.GetCultureInfo("nl"));

        Assert.Equal(Field, missing.Label);
        Assert.Equal(Field, missing.Labels!["nl"]);
        Assert.Equal(Field, missing.Labels["en"]);
        Assert.Contains(Field, text, StringComparison.Ordinal);
        Assert.DoesNotContain("#lsObjectModel_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nl", "Notitie")]
    [InlineData("en", "Note")]
    [InlineData("de", "Note")] // a language without a label falls back on English
    public async Task The_label_follows_the_profile_and_the_culture_of_the_call(string language, string expected)
    {
        var draft = await DraftAsync();

        var missing = Guard(new Profile()).Check(draft, CultureInfo.GetCultureInfo(language)).Single(m => m.Alias == Field);
        var text = SubmissionMessages.MissingFields([missing], CultureInfo.GetCultureInfo(language));

        Assert.Equal(expected, missing.Label);
        Assert.Equal("Notitie", missing.Labels!["nl"]);
        Assert.Equal("Note", missing.Labels["en"]);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("lsObjectModel", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_management_api_refuses_with_the_alias_and_structured_labels_but_never_the_key()
    {
        var draft = await DraftAsync();
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");

        using var response = await admin.PostJsonAsync($"/compliance/submission/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("lsObjectModel", raw, StringComparison.Ordinal);
        var problem = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        var field = problem.GetProperty("missingFields").EnumerateArray().Single(f => f.GetProperty("alias").GetString() == Field);
        Assert.Equal(Field, field.GetProperty("label").GetString());
        Assert.Equal(Field, field.GetProperty("labels").GetProperty("nl").GetString());
    }

    [Fact]
    public async Task Saving_the_submitted_status_through_the_management_api_never_puts_the_key_in_the_notification()
    {
        // Negative test: the save guard's own message, on the route that bypasses the submission endpoint.
        var draft = await DraftAsync();
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PutJsonAsync($"/document/{draft.Key}", new
        {
            template = (object?)null,
            values = new[]
            {
                new { alias = "title", culture = (string?)null, segment = (string?)null, value = (object?)"Wound care" },
                new { alias = SubmissionTestBed.StatusProperty, culture = (string?)null, segment = (string?)null, value = (object?)"submitted" },
            },
            variants = new[] { new { culture = (string?)null, segment = (string?)null, name = draft.Name } },
        });

        Assert.False(response.IsSuccessStatusCode);
        Assert.True(response.Headers.TryGetValues("Umb-Notifications", out var notifications));
        var text = Uri.UnescapeDataString(string.Concat(notifications));
        Assert.DoesNotContain("lsObjectModel", text, StringComparison.Ordinal);
    }
}
