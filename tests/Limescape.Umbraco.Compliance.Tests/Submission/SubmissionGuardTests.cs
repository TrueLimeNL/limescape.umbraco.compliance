using System.Net;
using Limescape.Umbraco.Compliance.Submission;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Submission;

/// <summary>
/// Required fields at submission. Required fields come from configuration (title, licence) and from
/// a schema snapshot (summary for this type; licence for all types; title only at the reviewer step).
/// </summary>
public sealed class SubmissionGuardTests(UmbracoTestSite site)
{
    private const string Submitted = "submitted";

    private IContentService ContentService => site.Services.GetRequiredService<IContentService>();

    [Fact]
    public async Task Submission_with_an_empty_required_field_is_refused_with_the_list_of_missing_fields()
    {
        var draft = await SubmissionTestBed.DraftAsync(site, ("title", "Wound care"), ("summary", "Short"));
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PostJsonAsync($"/compliance/submission/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.Equal(SubmissionErrorCodes.MissingRequiredFields, problem.GetProperty("code").GetString());
        var missing = problem.GetProperty("missingFields").EnumerateArray().Select(f => f.GetProperty("alias").GetString()).ToList();
        Assert.Equal(["licence"], missing);
        Assert.Contains("Licence", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task The_schema_snapshot_adds_required_fields_for_the_type_and_labels_follow_the_language()
    {
        var draft = await SubmissionTestBed.DraftAsync(site, ("title", "Wound care"), ("licence", "CC BY 4.0"));
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");

        using var response = await admin.GetPathAsync($"/compliance/submission/document/{draft.Key}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.JsonAsync();
        Assert.False(status.GetProperty("ready").GetBoolean());
        var field = Assert.Single(status.GetProperty("missingFields").EnumerateArray());
        Assert.Equal(("summary", "Samenvatting"), (field.GetProperty("alias").GetString(), field.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Submission_with_all_required_fields_sets_the_status()
    {
        var draft = await SubmissionTestBed.DraftAsync(site, ("title", "Wound care"), ("licence", "CC BY 4.0"), ("summary", "Short"));
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PostJsonAsync($"/compliance/submission/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("submitted").GetBoolean());
        Assert.Equal(Submitted, ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task Setting_the_submitted_status_through_the_management_api_without_required_fields_is_refused()
    {
        // Negative test: bypasses the submission endpoint and the UI, and saves the status directly.
        var draft = await SubmissionTestBed.DraftAsync(site, ("title", "Wound care"));
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
        Assert.True(response.Headers.TryGetValues("Umb-Notifications", out var notifications));
        Assert.Contains(SubmissionErrorCodes.MissingRequiredFields, string.Concat(notifications), StringComparison.Ordinal);
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task Setting_the_submitted_status_in_code_without_required_fields_is_cancelled()
    {
        var draft = await SubmissionTestBed.DraftAsync(site, ("licence", "CC BY 4.0"));
        draft.SetValue(SubmissionTestBed.StatusProperty, Submitted);

        var result = ContentService.Save(draft);

        Assert.False(result.Success);
        Assert.Null(ContentService.GetById(draft.Key)!.GetValue<string>(SubmissionTestBed.StatusProperty));
    }

    [Fact]
    public async Task Drafts_with_empty_required_fields_can_still_be_saved()
    {
        var draft = await SubmissionTestBed.DraftAsync(site);
        draft.SetValue("title", "Changed");

        Assert.True(ContentService.Save(draft).Success);
    }

    [Fact]
    public async Task The_required_fields_of_a_type_without_a_document_are_the_same_as_the_check_requires()
    {
        await SubmissionTestBed.TypeAsync(site); // writes the snapshot file the provider reads
        var guard = site.Services.GetRequiredService<ISubmissionGuard>();

        var required = guard.RequiredFieldsFor(SubmissionTestBed.DocumentType);

        Assert.Equal(["licence", "summary", "title"], required.Select(f => f.Alias).Order(StringComparer.Ordinal));
        Assert.Equal("Samenvatting", required.Single(f => f.Alias == "summary").Labels!["nl"]);
        Assert.Equal(["licence"], guard.RequiredFieldsFor("lsOtherType").Select(f => f.Alias)); // scope all only
    }

    [Theory]
    [InlineData("all", "lsVideo", true)]
    [InlineData("type:video", "lsVideo", true)]
    [InlineData("type:lsVideo", "lsVideo", true)]
    [InlineData("type:audio", "lsVideo", false)]
    [InlineData("step:reviewer", "lsVideo", false)]
    [InlineData("element", "lsVideo", false)]
    [InlineData("category:basic", "lsVideo", false)]
    [InlineData(null, "lsVideo", false)]
    public void Snapshot_scopes_that_apply_to_submission(string? scope, string typeAlias, bool applies) =>
        Assert.Equal(applies, SchemaSnapshotRequiredFieldProvider.Applies(scope, typeAlias));

    [Theory]
    [InlineData("""["submitted"]""", "submitted")]
    [InlineData("submitted", "submitted")]
    [InlineData("[]", null)]
    [InlineData("", null)]
    public void Status_codes_are_read_from_dropdown_and_text_values(string value, string? expected) =>
        Assert.Equal(expected, SubmissionGuard.Codes(value) is [var first, ..] ? first : null);
}
