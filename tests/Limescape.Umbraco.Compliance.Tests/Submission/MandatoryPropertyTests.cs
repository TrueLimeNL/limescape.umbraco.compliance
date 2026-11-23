using System.Net;
using Limescape.Umbraco.Compliance.Submission;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Submission;

/// <summary>
/// A property with Umbraco's <c>mandatory</c> flag on the document type (set by a metadata administrator) is
/// required at submission, next to the configured and snapshot fields, on every route. The status property is mandatory
/// on the test type as well, to prove system fields never count.
/// </summary>
public sealed class MandatoryPropertyTests(UmbracoTestSite site)
{
    private const string Submitted = "submitted";
    private const string DocumentType = "lsMandatoryTest";
    private const string Note = "mandatoryNote";

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
            var type = new ContentType(strings, Constants.System.Root) { Alias = DocumentType, Name = "Mandatory test", AllowedAsRoot = true };
            type.AddPropertyGroup("content", "Content");
            foreach (var alias in new[] { "title", Note, "optionalNote", SubmissionTestBed.StatusProperty })
            {
                type.AddPropertyType(
                    new PropertyType(strings, textstring, alias) { Name = char.ToUpperInvariant(alias[0]) + alias[1..], Mandatory = alias is Note or SubmissionTestBed.StatusProperty },
                    "content",
                    "Content");
            }

            var created = await site.Services.GetRequiredService<IContentTypeService>().CreateAsync(type, Constants.Security.SuperUserKey);
            Assert.True(created.Success, $"creating mandatory test type failed: {created.Result}");
            _type = type;
            return type;
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<IContent> DraftAsync(params (string Alias, string? Value)[] values)
    {
        var content = ContentService.Create($"Mandatory {Guid.NewGuid():N}", Constants.System.Root, await TypeAsync());
        foreach (var (alias, value) in values)
        {
            content.SetValue(alias, value);
        }

        Assert.True(ContentService.Save(content).Success);
        return content;
    }

    [Fact]
    public async Task A_mandatory_property_is_required_for_the_type_but_the_status_property_is_not()
    {
        await TypeAsync();
        var guard = site.Services.GetRequiredService<ISubmissionGuard>();

        var required = guard.RequiredFieldsFor(DocumentType).Select(f => f.Alias).ToList();

        // The guard returns the union of every provider; the test bed may configure other required fields (for example a
        // licence), so only the mandatory flag and the status exclusion are asserted here.
        Assert.Contains(Note, required);
        Assert.DoesNotContain(SubmissionTestBed.StatusProperty, required);
        Assert.DoesNotContain("optionalNote", required);
    }

    [Fact]
    public async Task Submission_without_a_mandatory_property_is_refused_with_its_name_and_with_a_value_it_succeeds()
    {
        var draft = await DraftAsync(("title", "Wound care"));
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var refused = await admin.PostJsonAsync($"/compliance/submission/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.JsonAsync();
        Assert.Equal(SubmissionErrorCodes.MissingRequiredFields, problem.GetProperty("code").GetString());
        Assert.Equal([Note], problem.GetProperty("missingFields").EnumerateArray().Select(f => f.GetProperty("alias").GetString()));
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));

        draft.SetValue(Note, "Filled");
        Assert.True(ContentService.Save(draft).Success);
        using var accepted = await admin.PostJsonAsync($"/compliance/submission/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Submitted, ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task Setting_the_submitted_status_in_code_without_the_mandatory_property_is_cancelled()
    {
        // Negative test: no endpoint, no UI.
        var draft = await DraftAsync(("title", "Wound care"));
        draft.SetValue(SubmissionTestBed.StatusProperty, Submitted);

        var result = ContentService.Save(draft);

        Assert.False(result.Success);
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task Setting_the_submitted_status_through_the_management_api_without_the_mandatory_property_is_refused()
    {
        var draft = await DraftAsync(("title", "Wound care"));
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PutJsonAsync($"/document/{draft.Key}", new
        {
            template = (object?)null,
            values = new[]
            {
                new { alias = "title", culture = (string?)null, segment = (string?)null, value = (object?)"Wound care" },
                new { alias = SubmissionTestBed.StatusProperty, culture = (string?)null, segment = (string?)null, value = (object?)Submitted },
            },
            variants = new[] { new { culture = (string?)null, segment = (string?)null, name = draft.Name } },
        });

        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task A_draft_without_the_mandatory_property_can_still_be_saved()
    {
        var draft = await DraftAsync();
        draft.SetValue("title", "Changed");

        Assert.True(ContentService.Save(draft).Success);
    }
}
