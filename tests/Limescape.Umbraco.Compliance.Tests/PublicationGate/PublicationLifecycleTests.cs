using System.Net;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate;

public sealed class PublicationLifecycleTests(UmbracoTestSite site)
{
    [Fact]
    public async Task System_fields_do_not_change_the_hash_but_content_does()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var hasher = site.Services.GetRequiredService<IContentVersionHasher>();
        var hash = hasher.ComputeHash(draft);
        draft.SetValue("status", "approved");
        draft.SetValue("owners", "[\"owner-1\"]");
        draft.SetValue("archivedAt", "2026-10-08");
        Assert.Equal(hash, hasher.ComputeHash(draft));
        using (LifecycleChangeScope.Begin(LifecycleRoutes.Archive))
        {
            Assert.True(bed.ContentService.Save(draft).Success);
        }

        Assert.Equal(hash, hasher.ComputeHash(bed.Reload(draft.Key)));
        draft.SetValue("title", "Changed content");
        Assert.NotEqual(hash, hasher.ComputeHash(draft));
    }

    [Fact]
    public async Task Excluded_aliases_are_configurable_and_case_insensitive()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var options = new PublicationGateOptions { ExcludedPropertyAliases = ["TITLE"] };
        var hasher = new ContentVersionHasher(new StaticOptionsMonitor<PublicationGateOptions>(options));
        var hash = hasher.ComputeHash(draft);
        draft.SetValue("title", "Unreviewed field in this custom configuration");
        Assert.Equal(hash, hasher.ComputeHash(draft));
        draft.SetValue("status", "submitted");
        Assert.NotEqual(hash, hasher.ComputeHash(draft));
    }

    [Fact]
    public async Task Configuration_binding_adds_custom_system_aliases_to_the_defaults()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExcludedPropertyAliases:0"] = "TITLE",
        }).Build().Get<PublicationGateOptions>()!;
        Assert.Contains("status", options.ExcludedPropertyAliases);
        Assert.Contains("owners", options.ExcludedPropertyAliases);
        Assert.Contains("archivedAt", options.ExcludedPropertyAliases);
        var hasher = new ContentVersionHasher(new StaticOptionsMonitor<PublicationGateOptions>(options));
        var hash = hasher.ComputeHash(draft);
        draft.SetValue("title", "Configured system field");
        draft.SetValue("status", "approved");
        Assert.Equal(hash, hasher.ComputeHash(draft));
    }

    [Theory]
    [InlineData("assistant", "approved")]
    [InlineData("job", "rejected")]
    [InlineData("backoffice", "approved")]
    [InlineData("system", "approved")]
    public async Task Acting_user_channels_other_than_app_or_api_cannot_decide(string channel, string decision)
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision }, TestIssuer.Shared.CreateToken(subject: "reviewer-1", channel: channel));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GateMessages.ApprovalChannelNotAllowed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("api")]
    public async Task Owner_cannot_approve_but_can_reject_and_another_reviewer_can_approve(string channel)
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        draft.SetValue("owners", "[\"owner-1\",\"co-owner\"]");
        bed.ContentService.Save(draft);
        using var denied = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved" }, TestIssuer.Shared.CreateToken(subject: "co-owner", channel: channel));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains(GateMessages.ApprovalOwnMaterial, await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        using var rejected = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "rejected", findings = "Licentie ontbreekt." }, TestIssuer.Shared.CreateToken(subject: "co-owner", channel: channel));
        Assert.Equal(HttpStatusCode.Created, rejected.StatusCode);
        using var approved = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved" }, TestIssuer.Shared.CreateToken(subject: "different-reviewer", channel: channel));
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);
    }

    [Fact]
    public async Task Four_eyes_can_be_disabled_by_configuration()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        draft.SetValue("owners", "[\"owner-1\"]");
        bed.ContentService.Save(draft);
        var options = site.Services.GetRequiredService<IOptionsMonitor<PublicationGateOptions>>().CurrentValue;
        Assert.True(options.RequireDifferentReviewer);
        try
        {
            options.RequireDifferentReviewer = false;
            using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
                new { decision = "approved" }, GateTestBed.ReviewerToken("owner-1"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            options.RequireDifferentReviewer = true;
        }
    }

    [Fact]
    public async Task Publish_persists_status_in_both_snapshots_and_unpublish_returns_to_approved()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var hasher = site.Services.GetRequiredService<IContentVersionHasher>();
        var hash = hasher.ComputeHash(draft);
        using var approval = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved", versionHash = hash }, GateTestBed.ReviewerToken());
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        using var published = await bed.Editor.PublishAsync(draft.Key);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var content = bed.Reload(draft.Key);
        Assert.Equal("published", content.GetValue<string>("status"));
        Assert.Equal("published", content.GetValue<string>("status", published: true));
        Assert.Equal(hash, hasher.ComputeHash(content));
        Assert.True(bed.ContentService.Unpublish(content).Success);
        Assert.Equal("approved", bed.Reload(draft.Key).GetValue<string>("status"));
        Assert.False(bed.Reload(draft.Key).Published);
    }

    [Fact]
    public async Task Unpublish_of_an_edited_draft_returns_to_draft()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        using var approval = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved" }, GateTestBed.ReviewerToken());
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        Assert.True(bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]).Success);
        var changed = bed.Reload(draft.Key);
        changed.SetValue("title", "New unapproved draft");
        bed.ContentService.Save(changed);
        Assert.True(bed.ContentService.Unpublish(bed.Reload(draft.Key)).Success);
        Assert.Equal("draft", bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task Management_api_cannot_write_published_status_without_publication()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        using var response = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{draft.Key}", new
        {
            variants = new[] { new { culture = (string?)null, segment = (string?)null, name = draft.Name } },
            values = new[] { new { culture = (string?)null, segment = (string?)null, alias = "status", value = "published" } },
            template = (object?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(GateMessages.PublicationRequiresApproval,
            response.Headers.TryGetValues("Umb-Notifications", out var values) ? string.Join(" ", values) : string.Empty);
        Assert.NotEqual("published", bed.Reload(draft.Key).GetValue<string>("status"));
        Assert.False(bed.Reload(draft.Key).Published);
    }
}
