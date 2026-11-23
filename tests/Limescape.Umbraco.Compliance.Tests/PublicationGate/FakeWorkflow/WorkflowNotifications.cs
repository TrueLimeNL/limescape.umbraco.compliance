using Umbraco.Cms.Core.Notifications;

namespace Limescape.Umbraco.Compliance.Tests.PublicationGate.FakeWorkflow;

// Look-alikes of the Umbraco Workflow 17 notifications: same type names and the same property names the package reads.

public sealed class FakeInstance
{
    public Guid? EntityKey { get; init; }

    public int NodeId { get; init; }

    /// <summary>1 publish, 2 unpublish.</summary>
    public int Type { get; init; }

    public string? EntityType { get; init; } = "document";
}

public sealed class WorkflowInstanceApprovingNotification(FakeInstance instance) : INotification
{
    public FakeInstance UpdatedEntity { get; } = instance;
}

public sealed class WorkflowInstanceCompletedNotification(FakeInstance instance) : INotification
{
    public FakeInstance CompletedInstance { get; } = instance;
}

public sealed class WorkflowInstanceUpdatedNotification(FakeInstance instance) : INotification
{
    public FakeInstance UpdatedEntity { get; } = instance;
}

public sealed class FakeTask
{
    /// <summary>2 is TaskStatus.Rejected in Umbraco Workflow.</summary>
    public int? Status { get; init; }

    public string? Comment { get; init; }
}

public sealed class FakeInstanceWithTasks
{
    public Guid? EntityKey { get; init; }

    public int NodeId { get; init; }

    public int Type { get; init; } = 1;

    public string? EntityType { get; init; } = "document";

    public List<FakeTask> TaskInstances { get; init; } = [];
}

public sealed class WorkflowInstanceRejectedNotification(FakeInstanceWithTasks instance) : INotification
{
    public FakeInstanceWithTasks UpdatedEntity { get; } = instance;
}
