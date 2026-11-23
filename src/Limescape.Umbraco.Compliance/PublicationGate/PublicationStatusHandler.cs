using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>Protects the lifecycle label on saves and resets it when a gated document is unpublished.</summary>
public sealed class PublicationStatusHandler(
    IGatedContentTypes gatedTypes,
    IContentService contentService,
    IContentVersionHasher hasher,
    IApprovalStore approvals,
    IOptionsMonitor<PublicationGateOptions> options)
    : INotificationHandler<ContentSavingNotification>, INotificationHandler<ContentUnpublishingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentSavingNotification notification)
    {
        var alias = options.CurrentValue.StatusPropertyAlias;
        foreach (var content in notification.SavedEntities.Where(gatedTypes.IsGated))
        {
            // Only the publication pipeline may introduce the published label.
            if (content.HasProperty(alias) && content.GetValue<string>(alias) == "published"
                && (content.Id == 0 || contentService.GetById(content.Key)?.GetValue<string>(alias) != "published"))
            {
                notification.CancelOperation(new EventMessage(GateMessages.PublicationRequiresApproval,
                    GateMessages.Text(GateMessages.PublicationRequiresApproval, null, content.Name), EventMessageType.Error));
            }
        }
    }

    /// <inheritdoc />
    public void Handle(ContentUnpublishingNotification notification)
    {
        var alias = options.CurrentValue.StatusPropertyAlias;
        foreach (var content in notification.UnpublishedEntities.Where(gatedTypes.IsGated))
        {
            if (content.HasProperty(alias) && content.GetValue<string>(alias) == "published")
            {
                content.SetValue(alias, approvals.GetLatest(content.Key, hasher.ComputeHash(content))?.Decision == ApprovalDecisions.Approved
                    ? "approved" : "draft");
            }
        }
    }
}
