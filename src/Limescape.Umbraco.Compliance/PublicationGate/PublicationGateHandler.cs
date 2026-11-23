using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// The publication gate. Umbraco raises <see cref="ContentPublishingNotification"/> for every publish route:
/// backoffice, Management API, scheduled publishing, publish with descendants and services called from code. For each
/// document of a gated type the handler cancels the publish unless the latest decision for exactly this draft is an
/// approval. A cancelled publish returns the code <see cref="GateMessages.PublicationRequiresApproval"/> as event message
/// (Management API: <c>400</c> with the message in the <c>Umb-Notifications</c> header) and is written to the audit log.
/// </summary>
public sealed partial class PublicationGateHandler(
    IGatedContentTypes gatedTypes,
    IContentVersionHasher hasher,
    IApprovalStore store,
    IApprovalService approvals,
    IWorkflowApprovalTracker workflowApprovals,
    IAuditWriter auditWriter,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    ILogger<PublicationGateHandler> logger,
    IOptionsMonitor<PublicationGateOptions> options,
    IOptionsMonitor<RetentionOptions> retentionOptions) : INotificationHandler<ContentPublishingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentPublishingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        foreach (var content in notification.PublishedEntities)
        {
            if (!gatedTypes.IsGated(content))
            {
                continue;
            }

            // Archiving took the material out of the public scope for good; its old approval does not bring it back.
            if (IsArchived(content))
            {
                notification.CancelOperation(new EventMessage(
                    GateMessages.PublicationArchived,
                    GateMessages.Text(GateMessages.PublicationArchived, GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language), content.Name),
                    EventMessageType.Error));
                continue;
            }

            var hash = hasher.ComputeHash(content);
            if (store.GetLatest(content.Key, hash)?.Decision == ApprovalDecisions.Approved)
            {
                continue;
            }

            // Umbraco Workflow publishes from inside the final approval; record that approval now, with the reviewer.
            if (workflowApprovals.TryTake(content.Key, hash, out var reviewer) && reviewer is not null)
            {
                approvals.RecordWorkflowApproval(content, hash, reviewer);
                continue;
            }

            var culture = GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language);
            notification.CancelOperation(new EventMessage(
                GateMessages.PublicationRequiresApproval,
                GateMessages.Text(GateMessages.PublicationRequiresApproval, culture, content.Name),
                EventMessageType.Error));

            auditWriter.Write(new AuditRecord(AuditActions.PublishBlocked, AuditEntityTypes.Document, content.Key)
            {
                Field = "versionHash",
                NewValue = hash,
            });

            LogCancelled(logger, content.Key, content.ContentType.Alias, content.VersionId, hash);
        }

        // Persist the lifecycle status with the publish, including its published snapshot.
        if (!notification.Cancel)
        {
            foreach (var content in notification.PublishedEntities.Where(gatedTypes.IsGated))
            {
                if (content.HasProperty(options.CurrentValue.StatusPropertyAlias))
                {
                    content.SetValue(options.CurrentValue.StatusPropertyAlias, "published");
                    // Umbraco prepared published values before raising this notification.
                    content.Properties[options.CurrentValue.StatusPropertyAlias]?.PublishValues(null, "*");
                }
            }
        }
    }

    private bool IsArchived(IContent content)
    {
        var archivedAlias = retentionOptions.CurrentValue.ArchivedDatePropertyAlias;
        return LifecycleStatuses.Of(content, options.CurrentValue.StatusPropertyAlias) == LifecycleStatuses.Archived
            || (!string.IsNullOrWhiteSpace(archivedAlias) && content.HasProperty(archivedAlias)
                && content.GetValue(archivedAlias) is { } value && !string.IsNullOrWhiteSpace(value.ToString()));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publication gate cancelled publishing of document {ContentKey} (type {ContentType}, version {VersionId}, hash {VersionHash}): no approval for this version")]
    private static partial void LogCancelled(ILogger logger, Guid contentKey, string contentType, int versionId, string versionHash);
}
