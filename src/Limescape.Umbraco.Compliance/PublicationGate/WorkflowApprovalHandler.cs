using System.Reflection;
using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Writes an approval record when an Umbraco Workflow approval flow for publishing a gated document completes,
/// and a rejection record when a reviewer rejects it.
/// Umbraco Workflow is a soft dependency: the package has no reference to it, the handlers are registered only when its
/// notification types can be loaded, and the instance is read by property name.
/// </summary>
/// <remarks>
/// Order inside Umbraco Workflow 17 when the last reviewer approves: <c>WorkflowInstanceApprovingNotification</c>, then the
/// publish (which passes the gate), then <c>WorkflowInstanceCompletedNotification</c>; a flow with a release date
/// completes without publishing and Umbraco's scheduler publishes later. So the approval is tracked when approving starts,
/// recorded by the gate when Workflow publishes, or recorded on completion when the publish is scheduled. An approval of
/// a step that does not complete the flow is forgotten at the end of the request and records nothing.
/// A rejection ("Afwijzen") raises <c>WorkflowInstanceRejectedNotification</c> after Workflow stored the reviewer's comment
/// on the task; the handler records a <c>rejected</c> decision with that comment as findings and moves the lifecycle
/// status to <c>rejected</c>, so the owner can change and resubmit the material through the Agent API as well. Workflow's
/// own setting "Mandatory comments" (on by default, administrators exempt) is what makes a rejection carry a comment;
/// the notification comes after the fact and cannot refuse it, so a rejection without a comment is still recorded
/// (findings empty) and logged as a warning.
/// </remarks>
public sealed partial class WorkflowApprovalHandler(
    IContentService contentService,
    IContentVersionHasher hasher,
    IApprovalStore store,
    IApprovalService approvals,
    IWorkflowApprovalTracker tracker,
    IActingUserAccessor actingUserAccessor,
    IGatedContentTypes gatedTypes,
    ILogger<WorkflowApprovalHandler> logger)
{
    /// <summary>Assembly that holds the Umbraco Workflow notifications.</summary>
    public const string WorkflowAssembly = "Umbraco.Workflow.Core";

    /// <summary>Namespace of the Umbraco Workflow notifications.</summary>
    public const string NotificationNamespace = "Umbraco.Workflow.Core.Notifications";

    /// <summary>Raised before each approval of a step; cancelable, so handlers run immediately.</summary>
    public const string ApprovingNotification = "WorkflowInstanceApprovingNotification";

    /// <summary>Raised when the flow is complete (after the publish, or after scheduling it).</summary>
    public const string CompletedNotification = "WorkflowInstanceCompletedNotification";

    /// <summary>Raised at the end of every action on an instance.</summary>
    public const string UpdatedNotification = "WorkflowInstanceUpdatedNotification";

    /// <summary>Raised after a reviewer rejected a step; the task carries the comment.</summary>
    public const string RejectedNotification = "WorkflowInstanceRejectedNotification";

    /// <summary><c>WorkflowType.Publish</c> in Umbraco Workflow.</summary>
    private const int PublishWorkflowType = 1;

    /// <summary>
    /// Registers a handler for each Umbraco Workflow notification that can be loaded, by name. Returns the number
    /// registered: 0 when Umbraco Workflow is not installed.
    /// </summary>
    public static int Register(IServiceCollection services, Func<string, Type?>? resolveType = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        resolveType ??= name => Type.GetType($"{NotificationNamespace}.{name}, {WorkflowAssembly}", throwOnError: false);

        var registered = 0;
        foreach (var name in new[] { ApprovingNotification, CompletedNotification, UpdatedNotification, RejectedNotification })
        {
            var notificationType = resolveType(name);
            if (notificationType is null || !typeof(INotification).IsAssignableFrom(notificationType))
            {
                continue;
            }

            services.AddTransient(
                typeof(INotificationHandler<>).MakeGenericType(notificationType),
                typeof(WorkflowNotificationRelay<>).MakeGenericType(notificationType));
            registered++;
        }

        return registered;
    }

    /// <summary>Handles one Umbraco Workflow notification, dispatched on its type name.</summary>
    public void Handle(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var instance = WorkflowInstanceInfo.From(notification);
        if (instance is null)
        {
            LogUnreadable(logger, notification.GetType().FullName);
            return;
        }

        switch (notification.GetType().Name)
        {
            case ApprovingNotification:
                OnApproving(instance);
                break;
            case CompletedNotification:
                OnCompleted(instance);
                break;
            case UpdatedNotification:
                OnUpdated(instance);
                break;
            case RejectedNotification:
                OnRejected(instance);
                break;
        }
    }

    private void OnApproving(WorkflowInstanceInfo instance)
    {
        // Only a person's approval counts; an API user without acting-user token or the system records nothing.
        var reviewer = actingUserAccessor.Current;
        if (ApprovalService.IsPerson(reviewer) && ApprovalService.IsDecisionChannelAllowed(reviewer)
            && GatedDocument(instance) is { } content && approvals.CanApprove(content, reviewer))
        {
            tracker.Begin(content.Key, hasher.ComputeHash(content), reviewer);
        }
    }

    private void OnCompleted(WorkflowInstanceInfo instance)
    {
        if (GatedDocument(instance) is not { } content)
        {
            return;
        }

        // Still pending: Workflow completed without publishing (release date), so the scheduler will publish later.
        var hash = hasher.ComputeHash(content);
        if (tracker.TryTake(content.Key, hash, out var reviewer) && reviewer is not null
            && store.GetLatest(content.Key, hash)?.Decision != ApprovalDecisions.Approved)
        {
            approvals.RecordWorkflowApproval(content, hash, reviewer);
        }
    }

    private void OnRejected(WorkflowInstanceInfo instance)
    {
        if (GatedDocument(instance) is not { } content)
        {
            return;
        }

        tracker.Clear(content.Key);

        // Like an approval, a rejection names a person; a system action records nothing.
        var reviewer = actingUserAccessor.Current;
        if (!ApprovalService.IsPerson(reviewer) || !ApprovalService.IsDecisionChannelAllowed(reviewer))
        {
            LogRejectionWithoutPerson(logger, content.Key);
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.Comment))
        {
            LogRejectionWithoutComment(logger, content.Key);
        }

        if (approvals.RecordWorkflowRejection(content, hasher.ComputeHash(content), reviewer, instance.Comment) is null)
        {
            LogRejectionNotRecorded(logger, content.Key);
        }
    }

    private void OnUpdated(WorkflowInstanceInfo instance)
    {
        if (instance.EntityKey is { } key)
        {
            tracker.Clear(key);
        }
        else if (contentService.GetById(instance.NodeId) is { } content)
        {
            tracker.Clear(content.Key);
        }
    }

    private IContent? GatedDocument(WorkflowInstanceInfo instance)
    {
        if (instance.Type != PublishWorkflowType
            || (instance.EntityType is not null && !string.Equals(instance.EntityType, "document", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var content = instance.EntityKey is { } key && key != Guid.Empty ? contentService.GetById(key) : contentService.GetById(instance.NodeId);
        return content is not null && gatedTypes.IsGated(content) ? content : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the workflow instance from {Notification}; no approval record is written (the publication gate stays closed)")]
    private static partial void LogUnreadable(ILogger logger, string? notification);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Umbraco Workflow rejected document {ContentKey} without a person; no rejection is recorded and the lifecycle status stays as it is")]
    private static partial void LogRejectionWithoutPerson(ILogger logger, Guid contentKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Umbraco Workflow rejected document {ContentKey} without a comment; the rejection is recorded without findings (turn on Workflow's mandatory comments)")]
    private static partial void LogRejectionWithoutComment(ILogger logger, Guid contentKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Umbraco Workflow rejection of document {ContentKey} was not recorded: another rule refused the status change")]
    private static partial void LogRejectionNotRecorded(ILogger logger, Guid contentKey);
}

/// <summary>
/// The fields of an Umbraco Workflow instance the package needs, read by property name.
/// </summary>
/// <param name="EntityKey">Key of the document, when the instance carries it.</param>
/// <param name="NodeId">Integer id of the document.</param>
/// <param name="Type">Workflow type: 1 publish, 2 unpublish.</param>
/// <param name="EntityType">Entity type, for example <c>document</c>.</param>
public sealed record WorkflowInstanceInfo(Guid? EntityKey, int NodeId, int Type, string? EntityType)
{
    /// <summary><c>TaskStatus.Rejected</c> in Umbraco Workflow.</summary>
    private const int RejectedTaskStatus = 2;

    /// <summary>Comment of the most recently rejected task, if any: the reviewer's feedback.</summary>
    public string? Comment { get; init; }

    private static readonly string[] InstanceProperties = ["CompletedInstance", "UpdatedEntity", "Target"];

    /// <summary>Reads the instance from a Workflow notification; null when its shape is not recognised.</summary>
    public static WorkflowInstanceInfo? From(object notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var instance = InstanceProperties
            .Select(name => notification.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(p => p is not null)
            .Select(p => p!.GetValue(notification))
            .FirstOrDefault(v => v is not null);
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        if (type.GetProperty("NodeId")?.GetValue(instance) is not int nodeId
            || type.GetProperty("Type")?.GetValue(instance) is not int workflowType)
        {
            return null;
        }

        return new WorkflowInstanceInfo(
            type.GetProperty("EntityKey")?.GetValue(instance) as Guid?,
            nodeId,
            workflowType,
            type.GetProperty("EntityType")?.GetValue(instance) as string)
        {
            Comment = RejectionComment(type.GetProperty("TaskInstances")?.GetValue(instance) as System.Collections.IEnumerable),
        };
    }

    private static string? RejectionComment(System.Collections.IEnumerable? tasks)
    {
        string? comment = null;
        foreach (var task in tasks ?? Array.Empty<object>())
        {
            var type = task?.GetType();
            if (type?.GetProperty("Status")?.GetValue(task) is int status && status == RejectedTaskStatus)
            {
                comment = type.GetProperty("Comment")?.GetValue(task) as string;
            }
        }

        return comment;
    }
}

/// <summary>
/// Closed over an Umbraco Workflow notification type at startup by <see cref="WorkflowApprovalHandler.Register"/>.
/// </summary>
internal sealed class WorkflowNotificationRelay<TNotification>(WorkflowApprovalHandler handler) : INotificationHandler<TNotification>
    where TNotification : INotification
{
    public void Handle(TNotification notification) => handler.Handle(notification);
}
