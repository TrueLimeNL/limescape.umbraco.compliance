using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Limescape.Umbraco.Compliance.Tests.PublicationGate.FakeWorkflow;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate;

/// <summary>
/// The Umbraco Workflow bridge. Umbraco Workflow is not installed in the test site, so these tests send
/// look-alike notifications with the same type names and property names, in the order Umbraco Workflow 17 raises them.
/// </summary>
public sealed class WorkflowApprovalHandlerTests(UmbracoTestSite site)
{
    [Fact]
    public void Nothing_is_registered_when_umbraco_workflow_is_not_installed()
    {
        var services = new ServiceCollection();

        Assert.Equal(0, WorkflowApprovalHandler.Register(services));
        Assert.Empty(services);
    }

    [Fact]
    public void Handlers_are_registered_for_the_workflow_notification_types_that_can_be_loaded()
    {
        var services = new ServiceCollection();
        var fakes = new Dictionary<string, Type>
        {
            [WorkflowApprovalHandler.ApprovingNotification] = typeof(WorkflowInstanceApprovingNotification),
            [WorkflowApprovalHandler.CompletedNotification] = typeof(WorkflowInstanceCompletedNotification),
            [WorkflowApprovalHandler.UpdatedNotification] = typeof(WorkflowInstanceUpdatedNotification),
        };

        var registered = WorkflowApprovalHandler.Register(services, name => fakes.GetValueOrDefault(name));

        Assert.Equal(3, registered);
        Assert.Contains(services, d => d.ServiceType == typeof(INotificationHandler<WorkflowInstanceApprovingNotification>));
        Assert.Contains(services, d => d.ServiceType == typeof(INotificationHandler<WorkflowInstanceCompletedNotification>));
    }

    [Fact]
    public async Task Final_approval_lets_the_workflow_publish_through_and_records_the_reviewer()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        PublishResult result;
        using (Request("reviewer-wf", "Wendy Workflow"))
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            // Umbraco Workflow publishes from inside the approve action, before "completed".
            result = bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]);
            Handler.Handle(new WorkflowInstanceCompletedNotification(Instance(draft)));
            Handler.Handle(new WorkflowInstanceUpdatedNotification(Instance(draft)));
        }

        Assert.True(result.Success, result.Result.ToString());
        var record = Assert.Single(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        Assert.Equal(ApprovalSources.Workflow, record.Source);
        Assert.Equal("reviewer-wf", record.ReviewerId);
        Assert.Equal("Wendy Workflow", record.ReviewerName);
    }

    [Fact]
    public async Task Approval_of_a_step_that_does_not_complete_the_flow_records_nothing()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using (Request("reviewer-step-1", "Step One"))
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            Handler.Handle(new WorkflowInstanceUpdatedNotification(Instance(draft)));

            // A direct publish later in the same request is not a Workflow approval.
            var result = bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]);
            Assert.Equal(PublishResultType.FailedPublishCancelledByEvent, result.Result);
        }

        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Fact]
    public async Task Flow_with_a_release_date_records_the_approval_on_completion_for_the_scheduler()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using (Request("reviewer-later", "Lara Later"))
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            Handler.Handle(new WorkflowInstanceCompletedNotification(Instance(draft)));
            Handler.Handle(new WorkflowInstanceUpdatedNotification(Instance(draft)));
        }

        var record = Assert.Single(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
        Assert.Equal("reviewer-later", record.ReviewerId);
        Assert.True(bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]).Success);
    }

    [Fact]
    public async Task Workflow_approval_does_not_cover_a_draft_that_changed_before_the_publish()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();

        using (Request("reviewer-wf", "Wendy Workflow"))
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            var changed = bed.Reload(draft.Key);
            changed.SetValue("title", "Changed between approval and publish");
            bed.ContentService.Save(changed);

            var result = bed.ContentService.Publish(changed, ["*"]);

            Assert.Equal(PublishResultType.FailedPublishCancelledByEvent, result.Result);
        }
    }

    [Fact]
    public async Task Workflow_approval_without_a_person_records_nothing_and_the_gate_stays_closed()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        var accessor = site.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        try
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            var result = bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]);

            Assert.Equal(PublishResultType.FailedPublishCancelledByEvent, result.Result);
        }
        finally
        {
            accessor.HttpContext = null;
        }

        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    [Fact]
    public async Task Unpublish_flows_and_open_document_types_are_ignored()
    {
        var bed = await GateTestBed.GetAsync(site);
        var gated = bed.CreateDraft();
        var open = bed.CreateDraft(GateTestBed.OpenType);

        using (Request("reviewer-wf", "Wendy Workflow"))
        {
            Handler.Handle(new WorkflowInstanceCompletedNotification(Instance(gated, workflowType: 2)));
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(open)));
            Handler.Handle(new WorkflowInstanceCompletedNotification(Instance(open)));
        }

        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(gated.Key));
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(open.Key));
    }

    [Fact]
    public void Instance_is_read_by_property_name_and_unknown_shapes_give_null()
    {
        var key = Guid.NewGuid();

        var info = WorkflowInstanceInfo.From(new WorkflowInstanceCompletedNotification(new FakeInstance { EntityKey = key, NodeId = 12, Type = 1 }));

        Assert.Equal(new WorkflowInstanceInfo(key, 12, 1, "document"), info);
        Assert.Null(WorkflowInstanceInfo.From(new object()));
    }

    [Theory]
    [InlineData("owner-wf", "app")]
    [InlineData("other-wf", "assistant")]
    public async Task Workflow_cannot_bypass_owner_or_channel_rules(string personId, string channel)
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft();
        draft.SetValue("owners", "[\"owner-wf\"]");
        bed.ContentService.Save(draft);
        using (Request(personId, "Workflow person", channel))
        {
            Handler.Handle(new WorkflowInstanceApprovingNotification(Instance(draft)));
            Assert.False(bed.ContentService.Publish(bed.Reload(draft.Key), ["*"]).Success);
            Handler.Handle(new WorkflowInstanceCompletedNotification(Instance(draft)));
        }
        Assert.Empty(site.Services.GetRequiredService<IApprovalStore>().GetAll(draft.Key));
    }

    private WorkflowApprovalHandler Handler => site.Services.GetRequiredService<WorkflowApprovalHandler>();

    private static FakeInstance Instance(IContent content, int workflowType = 1) =>
        new() { EntityKey = content.Key, NodeId = content.Id, Type = workflowType };

    /// <summary>Simulates the approve request of a person who signed in through a trusted integration.</summary>
    private Reset Request(string personId, string name, string channel = "app")
    {
        var accessor = site.Services.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        var now = DateTimeOffset.UtcNow;
        context.Features.Set(new ActingUserTokenFeature(
            new ActingUserToken(personId, name, channel, null, TestIssuer.Shared.Issuer, now, now.AddSeconds(30))));
        accessor.HttpContext = context;
        return new Reset(accessor);
    }

    private sealed class Reset(IHttpContextAccessor accessor) : IDisposable
    {
        public void Dispose() => accessor.HttpContext = null;
    }
}
