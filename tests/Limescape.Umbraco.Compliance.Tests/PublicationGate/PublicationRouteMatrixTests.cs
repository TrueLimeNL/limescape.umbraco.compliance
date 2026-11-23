using System.Net;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentPublishing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate;

/// <summary>
/// Route matrix for the publication gate: every route that can publish a gated document is cancelled
/// without an approval for exactly that version. None of these tests go through a UI.
/// </summary>
public sealed class PublicationRouteMatrixTests(UmbracoTestSite site)
{
    // ---- Management API (integrations, and the endpoint the backoffice itself calls) ----

    [Fact]
    public async Task Management_api_publish_without_approval_fails_with_error_code_and_stays_unpublished()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using var response = await bed.Editor.PublishAsync(draft.Key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(GateMessages.PublicationRequiresApproval, NotificationsHeader(response), StringComparison.Ordinal);
        Assert.False(bed.Reload(draft.Key).Published);
        Assert.Contains(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.PublishBlocked);
    }

    [Fact]
    public async Task Management_api_publish_after_reviewer_approval_succeeds()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);

        using var response = await bed.Editor.PublishAsync(draft.Key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(bed.Reload(draft.Key).Published);
    }

    [Fact]
    public async Task Change_after_approval_needs_a_new_approval()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);

        // Umbraco keeps the version id while a draft is saved again; the gate must still see the change.
        var edited = bed.Reload(draft.Key);
        edited.SetValue("title", "Edited after approval");
        bed.ContentService.Save(edited);
        Assert.Equal(draft.VersionId, bed.Reload(draft.Key).VersionId);

        using (var cancelled = await bed.Editor.PublishAsync(draft.Key))
        {
            Assert.Equal(HttpStatusCode.BadRequest, cancelled.StatusCode);
            Assert.False(bed.Reload(draft.Key).Published);
        }

        await ApproveAsync(bed, draft.Key);
        using var published = await bed.Editor.PublishAsync(draft.Key);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    [Fact]
    public async Task Edit_after_publishing_needs_a_new_approval_for_the_next_publish()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);
        using (var first = await bed.Editor.PublishAsync(draft.Key))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var next = bed.Reload(draft.Key);
        next.SetValue("title", "Version 2");
        bed.ContentService.Save(next);

        using var second = await bed.Editor.PublishAsync(draft.Key);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("First draft", bed.Reload(draft.Key).GetValue<string>("title", published: true));
    }

    [Fact]
    public async Task Rejection_after_approval_closes_the_gate_again()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);
        await ApproveAsync(bed, draft.Key, ApprovalDecisions.Rejected);

        using var response = await bed.Editor.PublishAsync(draft.Key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Document_type_that_is_not_gated_publishes_without_approval()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.OpenType);

        using var response = await bed.Editor.PublishAsync(draft.Key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Type_composed_of_a_gated_composition_is_gated()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.ComposedType);

        using (var cancelled = await bed.Editor.PublishAsync(draft.Key))
        {
            Assert.Equal(HttpStatusCode.BadRequest, cancelled.StatusCode);
        }

        await ApproveAsync(bed, draft.Key);
        using var published = await bed.Editor.PublishAsync(draft.Key);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    // ---- Backoffice person: the content publishing service the Management API calls for a signed-in user ----

    [Fact]
    public async Task Backoffice_user_publish_without_approval_is_cancelled()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        var result = await PublishAsBackofficeUser(bed, draft.Key);

        Assert.False(result.Success);
        Assert.Equal(ContentPublishingOperationStatus.CancelledByEvent, result.Status);
        Assert.False(bed.Reload(draft.Key).Published);
    }

    [Fact]
    public async Task Backoffice_user_publish_with_approval_succeeds()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);

        var result = await PublishAsBackofficeUser(bed, draft.Key);

        Assert.True(result.Success, result.Status.ToString());
        Assert.True(bed.Reload(draft.Key).Published);
    }

    // ---- Code, bulk and background routes on IContentService ----

    [Fact]
    public async Task Service_call_from_code_without_approval_is_cancelled_with_the_message()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        var result = bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]);

        Assert.Equal(PublishResultType.FailedPublishCancelledByEvent, result.Result);
        Assert.Contains(result.EventMessages?.GetAll() ?? [], m => m.Category == GateMessages.PublicationRequiresApproval);
    }

    [Fact]
    public async Task Scheduled_publish_without_approval_is_cancelled_and_logged()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        Schedule(bed, draft);

        bed.ContentService.PerformScheduledPublish(DateTime.UtcNow);

        Assert.False(bed.Reload(draft.Key).Published);
        Assert.Contains(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.PublishBlocked);
    }

    [Fact]
    public async Task Scheduled_publish_with_approval_publishes()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAsync(bed, draft.Key);
        Schedule(bed, draft);

        bed.ContentService.PerformScheduledPublish(DateTime.UtcNow);

        Assert.True(bed.Reload(draft.Key).Published);
    }

    [Fact]
    public async Task Publish_with_descendants_skips_the_unapproved_gated_child()
    {
        var bed = await GateTestBed.GetAsync(site);
        var parent = bed.CreateDraft(GateTestBed.OpenType);
        var approvedChild = bed.CreateDraft(parentId: parent.Id);
        var unapprovedChild = bed.CreateDraft(parentId: parent.Id);
        await ApproveAsync(bed, approvedChild.Key);

        bed.ContentService.PublishBranch(bed.Reload(parent.Key), PublishBranchFilter.IncludeUnpublished, ["*"]);

        Assert.True(bed.Reload(parent.Key).Published);
        Assert.True(bed.Reload(approvedChild.Key).Published);
        Assert.False(bed.Reload(unapprovedChild.Key).Published);
    }

    private static async Task ApproveAsync(GateTestBed bed, Guid key, string decision = ApprovalDecisions.Approved)
    {
        using var response = await bed.Reviewer.SendAsync(
            HttpMethod.Post, $"/compliance/approval/document/{key}",
            new { decision, comment = decision == ApprovalDecisions.Rejected ? "Bronvermelding ontbreekt." : null }, actingUserToken: GateTestBed.ReviewerToken());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Attempt<ContentPublishingResult, ContentPublishingOperationStatus>> PublishAsBackofficeUser(GateTestBed bed, Guid key) =>
        await bed.Site.Services.GetRequiredService<IContentPublishingService>()
            .PublishAsync(key, [new CulturePublishScheduleModel { Culture = null }], bed.BackofficeEditorKey);

    private static void Schedule(GateTestBed bed, IContent draft)
    {
        var content = bed.Reload(draft.Key);
        bed.ContentService.PersistContentSchedule(content, ContentScheduleCollection.CreateWithEntry(DateTime.UtcNow.AddMinutes(-1), null));
    }

    private static string NotificationsHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Umb-Notifications", out var values) ? string.Join(" ", values) : string.Empty;
}
