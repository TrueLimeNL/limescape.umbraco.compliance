using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate;

/// <summary>
/// The approval API over HTTP with API users, as an integration calls it.
/// </summary>
public sealed class ApprovalApiTests(UmbracoTestSite site)
{
    [Fact]
    public async Task Status_reports_the_gate_and_the_current_draft_without_a_decision()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        var status = await StatusAsync(bed, draft.Key);

        Assert.True(status.GetProperty("gated").GetBoolean());
        Assert.Equal(GateTestBed.GatedType, status.GetProperty("documentType").GetString());
        Assert.Equal(draft.VersionId, status.GetProperty("versionId").GetInt32());
        Assert.Equal(64, status.GetProperty("versionHash").GetString()!.Length);
        Assert.False(status.GetProperty("approved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("currentDecision").ValueKind);
    }

    [Fact]
    public async Task Reviewer_approval_is_stored_with_hash_person_date_and_findings()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var hash = (await StatusAsync(bed, draft.Key)).GetProperty("versionHash").GetString();

        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post,
            $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved", versionHash = hash, comment = "Looks good", findings = "Licence present; alt texts complete" },
            actingUserToken: GateTestBed.ReviewerToken(subject: "reviewer-3", name: "Rita Reviewer"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var record = Assert.Single(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        Assert.Equal(ApprovalDecisions.Approved, record.Decision);
        Assert.Equal(hash, record.VersionHash);
        Assert.Equal(draft.VersionId, record.VersionId);
        Assert.Equal("reviewer-3", record.ReviewerId);
        Assert.Equal("Rita Reviewer", record.ReviewerName);
        Assert.Equal(ApprovalSources.Integration, record.Source);
        Assert.Equal("Looks good", record.Comment);
        Assert.Equal("Licence present; alt texts complete", record.Findings);
        Assert.True((DateTime.UtcNow - record.DecidedUtc).Duration() < TimeSpan.FromMinutes(1));
        Assert.True((await StatusAsync(bed, draft.Key)).GetProperty("approved").GetBoolean());
        Assert.Contains(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.Approval && e.ActorId == "reviewer-3");
    }

    [Fact]
    public async Task User_without_reviewer_permission_cannot_approve_and_nothing_is_stored()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "approved" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GateMessages.ApprovalRequiresReviewer, await CodeAsync(response));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Fact]
    public async Task Api_user_in_a_reviewer_group_cannot_approve_without_naming_the_person()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "approved" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GateMessages.ApprovalRequiresActingUser, await CodeAsync(response));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        using var publish = await bed.Editor.PublishAsync(draft.Key);
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public void Only_a_backoffice_user_or_the_person_in_an_acting_user_token_counts_as_a_person()
    {
        Assert.True(ApprovalService.IsPerson(new Actor("u-1", "Editor", ActorChannels.Backoffice)));
        Assert.True(ApprovalService.IsPerson(new Actor("p-1", "Person", "app") { Issuer = TestIssuer.Shared.Issuer }));
        Assert.False(ApprovalService.IsPerson(new Actor("api-1", "Integration", ActorChannels.Api)));
        Assert.False(ApprovalService.IsPerson(Actor.System()));
    }

    [Fact]
    public async Task Approval_of_a_version_that_changed_since_review_is_refused_with_409()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var reviewedHash = (await StatusAsync(bed, draft.Key)).GetProperty("versionHash").GetString();
        var edited = bed.Reload(draft.Key);
        edited.SetValue("title", "Changed while under review");
        bed.ContentService.Save(edited);

        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "approved", versionHash = reviewedHash }, actingUserToken: GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(GateMessages.ApprovalVersionChanged, await CodeAsync(response));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Fact]
    public async Task Unknown_decision_is_refused_with_400()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "maybe" }, actingUserToken: GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GateMessages.ApprovalInvalidDecision, await CodeAsync(response));
    }

    [Fact]
    public async Task Unknown_document_gives_404()
    {
        var bed = await GateTestBed.GetAsync(site);

        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{Guid.NewGuid()}", new { decision = "approved" }, actingUserToken: GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approval_without_a_token_is_unauthorized()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        using var client = site.CreateHttpsClient();

        using var response = await client.PostAsync(
            new Uri($"{GateTestBed.ManagementApi}/compliance/approval/document/{draft.Key}", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { decision = "approved" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Fact]
    public async Task Integration_with_acting_user_token_records_the_person_as_reviewer()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var token = TestIssuer.Shared.CreateToken(subject: "reviewer-7", name: "Robin Reviewer", channel: "app", correlationId: "cid-approve");

        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "approved" }, actingUserToken: token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var record = Assert.Single(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        Assert.Equal("reviewer-7", record.ReviewerId);
        Assert.Equal("Robin Reviewer", record.ReviewerName);
        Assert.Equal("app", record.Channel);
        Assert.Equal(ApprovalSources.Integration, record.Source);
        Assert.Equal(bed.Reviewer.UserKey, record.TechnicalUserKey);
        Assert.Equal("cid-approve", record.CorrelationId);

        var audit = Assert.Single(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.Approval);
        Assert.Equal("reviewer-7", audit.ActorId);
        Assert.Equal(TestIssuer.Shared.Issuer, audit.Issuer);
    }

    [Fact]
    public async Task Integration_api_user_without_reviewer_group_cannot_approve_even_with_a_token()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var token = TestIssuer.Shared.CreateToken(subject: "reviewer-7");

        using var response = await bed.Editor.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{draft.Key}", new { decision = "approved" }, actingUserToken: token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    private static async Task<JsonElement> StatusAsync(GateTestBed bed, Guid key)
    {
        using var response = await bed.Editor.SendAsync(HttpMethod.Get, $"/compliance/approval/document/{key}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
