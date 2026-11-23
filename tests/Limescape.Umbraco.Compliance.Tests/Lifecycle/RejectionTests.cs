using System.Net;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Limescape.Umbraco.Compliance.Tests.PublicationGate.FakeWorkflow;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Lifecycle;

/// <summary>
/// A rejection, in Umbraco Workflow or through the approval API, is recorded with its feedback and moves the
/// lifecycle status to rejected; nothing else can set that status.
/// </summary>
public sealed class RejectionTests(UmbracoTestSite site)
{
    [Fact]
    public void The_rejected_notification_is_registered_when_umbraco_workflow_is_installed()
    {
        var services = new ServiceCollection();

        WorkflowApprovalHandler.Register(services, name => name == WorkflowApprovalHandler.RejectedNotification ? typeof(WorkflowInstanceRejectedNotification) : null);

        Assert.Contains(services, d => d.ServiceType == typeof(INotificationHandler<WorkflowInstanceRejectedNotification>));
    }

    [Fact]
    public async Task Workflow_rejection_records_the_comment_as_findings_and_moves_the_status_to_rejected()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);

        using (Request("reviewer-wf", "Wendy Workflow"))
        {
            Handler.Handle(new WorkflowInstanceRejectedNotification(Instance(draft,
                new FakeTask { Status = 1, Comment = "Earlier step approved" },
                new FakeTask { Status = 2, Comment = "De bronvermelding ontbreekt." })));
        }

        var record = Assert.Single(Store.GetAll(draft.Key));
        Assert.Equal(ApprovalDecisions.Rejected, record.Decision);
        Assert.Equal(ApprovalSources.Workflow, record.Source);
        Assert.Equal("De bronvermelding ontbreekt.", record.Findings);
        Assert.Equal("reviewer-wf", record.ReviewerId);
        Assert.Equal(LifecycleStatuses.Rejected, bed.Reload(draft.Key).GetValue<string>("status"));
        var audit = ComplianceLogs.AuditFor(site, draft.Key);
        Assert.Contains(audit, a => a.Action == AuditActions.Approval && a.NewValue!.StartsWith("rejected", StringComparison.Ordinal) && a.ActorId == "reviewer-wf");
        Assert.Contains(audit, a => a.Action == AuditActions.Update && a.Field == "status" && a.NewValue == "rejected" && a.ActorId == "reviewer-wf");
    }

    [Fact]
    public async Task After_a_workflow_rejection_the_owner_changes_the_draft_and_resubmits_and_the_gate_stays_closed()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);
        using (Request("reviewer-wf", "Wendy Workflow"))
        {
            Handler.Handle(new WorkflowInstanceRejectedNotification(Instance(draft, new FakeTask { Status = 2, Comment = "Titel te vaag." })));
        }

        Assert.Equal(PublishResultType.FailedPublishCancelledByEvent, bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]).Result);

        var changed = bed.Reload(draft.Key);
        changed.SetValue("title", "Wondzorg bij diabetes: stappenplan");
        changed.SetValue("status", "submitted");
        Assert.True(bed.ContentService.Save(changed).Success);

        Assert.Equal("submitted", bed.Reload(draft.Key).GetValue<string>("status"));
        Assert.Equal(ApprovalDecisions.Rejected, Assert.Single(Store.GetAll(draft.Key)).Decision);
    }

    [Fact]
    public async Task Workflow_rejection_without_a_person_records_nothing_and_keeps_the_status()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);
        var accessor = site.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        try
        {
            Handler.Handle(new WorkflowInstanceRejectedNotification(Instance(draft, new FakeTask { Status = 2, Comment = "No person" })));
        }
        finally
        {
            accessor.HttpContext = null;
        }

        Assert.Empty(Store.GetAll(draft.Key));
        Assert.Equal("submitted", bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task Workflow_rejection_without_a_comment_is_still_recorded_because_workflow_already_rejected()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);

        using (Request("reviewer-admin", "Admin Without Comment"))
        {
            Handler.Handle(new WorkflowInstanceRejectedNotification(Instance(draft, new FakeTask { Status = 2, Comment = "  " })));
        }

        var record = Assert.Single(Store.GetAll(draft.Key));
        Assert.Equal(ApprovalDecisions.Rejected, record.Decision);
        Assert.Null(record.Findings);
        Assert.Equal(LifecycleStatuses.Rejected, bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "   ")]
    public async Task Rejection_through_the_api_without_feedback_is_refused(string? comment, string? findings)
    {
        // Negative test: the approval API directly, as an integration, without any screen.
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);

        using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "rejected", comment, findings }, GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(GateMessages.ApprovalRejectionRequiresFeedback, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Store.GetAll(draft.Key));
        Assert.Equal("submitted", bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task Rejection_through_the_api_with_feedback_is_recorded_and_moves_the_status_to_rejected()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);

        using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "rejected", comment = "Graag aanpassen", findings = "Licentie ontbreekt." }, GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var record = Assert.Single(Store.GetAll(draft.Key));
        Assert.Equal("Licentie ontbreekt.", record.Findings);
        Assert.Equal(ApprovalSources.Integration, record.Source);
        Assert.Equal(LifecycleStatuses.Rejected, bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task A_rejection_of_a_new_draft_of_a_published_document_keeps_the_published_status()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        await ApproveAndPublishAsync(bed, draft);
        var next = bed.Reload(draft.Key);
        next.SetValue("title", "Second draft");
        Assert.True(bed.ContentService.Save(next).Success);

        using var response = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "rejected", comment = "Second draft not ready" }, GateTestBed.ReviewerToken());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(LifecycleStatuses.Published, bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task Setting_the_status_rejected_in_code_or_through_the_management_api_is_refused()
    {
        // Negative test: both routes bypass the review decision and every screen.
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);
        var direct = bed.Reload(draft.Key);
        direct.SetValue("status", "rejected");

        var saved = bed.ContentService.Save(direct);
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var put = await admin.PutJsonAsync($"/document/{draft.Key}", DocumentBody(draft, ("status", "rejected")));

        Assert.False(saved.Success);
        Assert.Contains(saved.EventMessages!.GetAll(), m => m.Category == GateMessages.LifecycleRejectionRequiresDecision);
        Assert.False(put.IsSuccessStatusCode);
        Assert.True(put.Headers.TryGetValues("Umb-Notifications", out var notifications));
        Assert.Contains(GateMessages.LifecycleRejectionRequiresDecision, string.Concat(notifications), StringComparison.Ordinal);
        Assert.Equal("submitted", bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task A_decision_for_a_person_the_route_vouches_for_writes_the_same_record_and_status_as_the_approval_api()
    {
        // The Agent API checks the reviewer role from the person's own token and records through DecideAs.
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);
        var reviewer = new Actor("reviewer-agent", "Ria Agent", "assistant") { Issuer = TestIssuer.Shared.Issuer };

        Attempt<ApprovalRecord?, ApprovalOperationStatus> decided;
        using (Request("reviewer-agent", "Ria Agent"))
        {
            decided = Approvals.DecideAs(bed.Reload(draft.Key), new ApprovalDecision(ApprovalDecisions.Rejected, Findings: "Doelgroep ontbreekt."), reviewer);
        }

        Assert.Equal(ApprovalOperationStatus.Success, decided.Status);
        var record = Assert.Single(Store.GetAll(draft.Key));
        Assert.Equal(ApprovalSources.Integration, record.Source);
        Assert.Equal("assistant", record.Channel);
        Assert.Equal("Doelgroep ontbreekt.", record.Findings);
        Assert.Equal(LifecycleStatuses.Rejected, bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Fact]
    public async Task A_decision_without_a_token_person_by_an_owner_or_a_rejection_without_feedback_records_nothing()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = SubmittedDraft(bed);
        var content = bed.Reload(draft.Key);

        var noToken = Approvals.DecideAs(content, new ApprovalDecision(ApprovalDecisions.Approved), new Actor("someone", "Someone", ActorChannels.Backoffice));
        var owner = Approvals.DecideAs(content, new ApprovalDecision(ApprovalDecisions.Approved), new Actor("owner-1", "Owner", "app") { Issuer = TestIssuer.Shared.Issuer });
        var noFeedback = Approvals.DecideAs(content, new ApprovalDecision(ApprovalDecisions.Rejected, Comment: " "), new Actor("reviewer-x", "Reviewer", "app") { Issuer = TestIssuer.Shared.Issuer });

        Assert.Equal(ApprovalOperationStatus.ActingUserRequired, noToken.Status);
        Assert.Equal(ApprovalOperationStatus.OwnMaterial, owner.Status);
        Assert.Equal(ApprovalOperationStatus.RejectionRequiresFeedback, noFeedback.Status);
        Assert.Empty(Store.GetAll(draft.Key));
        Assert.Equal("submitted", bed.Reload(draft.Key).GetValue<string>("status"));
    }

    /// <summary>Body of a Management API <c>PUT /document/{id}</c> for an invariant gate test document.</summary>
    internal static object DocumentBody(IContent document, params (string Alias, object? Value)[] values) => new
    {
        template = (object?)null,
        values = new[] { ("title", (object?)document.GetValue<string>("title")) }.Concat(values)
            .Select(v => new { alias = v.Item1, culture = (string?)null, segment = (string?)null, value = v.Item2 }).ToArray(),
        variants = new[] { new { culture = (string?)null, segment = (string?)null, name = document.Name } },
    };

    /// <summary>Approves the current draft through the approval API and publishes it through the Management API.</summary>
    internal static async Task ApproveAndPublishAsync(GateTestBed bed, IContent draft)
    {
        var hash = bed.Site.Services.GetRequiredService<IContentVersionHasher>().ComputeHash(bed.Reload(draft.Key));
        using var approval = await bed.Reviewer.SendAsync(HttpMethod.Post, $"/compliance/approval/document/{draft.Key}",
            new { decision = "approved", versionHash = hash }, GateTestBed.ReviewerToken("reviewer-publish", "Paula Publisher"));
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        using var published = await bed.Editor.PublishAsync(draft.Key);
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    internal static IContent SubmittedDraft(GateTestBed bed)
    {
        var draft = bed.CreateDraft();
        draft.SetValue("owners", "[\"owner-1\"]");
        draft.SetValue("status", "submitted");
        Assert.True(bed.ContentService.Save(draft).Success);
        return draft;
    }

    private WorkflowApprovalHandler Handler => site.Services.GetRequiredService<WorkflowApprovalHandler>();

    private IApprovalStore Store => site.Services.GetRequiredService<IApprovalStore>();

    private IApprovalService Approvals => site.Services.GetRequiredService<IApprovalService>();

    private static FakeInstanceWithTasks Instance(IContent content, params FakeTask[] tasks) =>
        new() { EntityKey = content.Key, NodeId = content.Id, TaskInstances = [.. tasks] };

    /// <summary>The reject request of a person who signed in through a trusted integration.</summary>
    private Reset Request(string personId, string name)
    {
        var accessor = site.Services.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        var now = DateTimeOffset.UtcNow;
        context.Features.Set(new ActingUserTokenFeature(
            new ActingUserToken(personId, name, "app", null, TestIssuer.Shared.Issuer, now, now.AddSeconds(30))));
        accessor.HttpContext = context;
        return new Reset(accessor);
    }

    private sealed class Reset(IHttpContextAccessor accessor) : IDisposable
    {
        public void Dispose() => accessor.HttpContext = null;
    }
}
