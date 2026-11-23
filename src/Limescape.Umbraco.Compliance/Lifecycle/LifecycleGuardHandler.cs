using System.Globalization;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Lifecycle;

/// <summary>
/// Protects the lifecycle of gated documents in the content layer: only the archive route may set the archive
/// date or move the status into or out of <c>archived</c>, and only a recorded rejection may move it to
/// <c>rejected</c>. Every other save that tries is cancelled, through every route that saves (backoffice, Management
/// API, integrations, code), with <see cref="GateMessages.LifecycleArchiveRequiresRoute"/> or
/// <see cref="GateMessages.LifecycleRejectionRequiresDecision"/>. The <c>published</c> label is protected by
/// <see cref="PublicationStatusHandler"/>.
/// </summary>
/// <remarks>
/// A Management API <c>PUT</c> on a label property (as in the content layer host's object model) never reaches this
/// handler with a new value: Umbraco's label editor drops incoming values, so that request answers 200 and changes
/// nothing. A property that does accept values (for example a text property) is refused here with 400.
/// </remarks>
public sealed class LifecycleGuardHandler(
    IGatedContentTypes gatedTypes,
    IContentService contentService,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    IOptionsMonitor<PublicationGateOptions> gateOptions,
    IOptionsMonitor<RetentionOptions> retentionOptions) : INotificationHandler<ContentSavingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentSavingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var statusAlias = gateOptions.CurrentValue.StatusPropertyAlias;
        var archivedAlias = retentionOptions.CurrentValue.ArchivedDatePropertyAlias;

        foreach (var content in notification.SavedEntities.Where(c => !c.Blueprint && gatedTypes.IsGated(c)))
        {
            var stored = content.HasIdentity ? contentService.GetById(content.Key) : null;
            var code = Violation(content, stored, statusAlias, archivedAlias);
            if (code is null)
            {
                continue;
            }

            var culture = GateMessages.CultureOf(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Language);
            notification.CancelOperation(new EventMessage(code, GateMessages.Text(code, culture, content.Name), EventMessageType.Error));
            return;
        }
    }

    /// <summary>The error code for a protected change from <paramref name="stored"/> to <paramref name="content"/>, or null.</summary>
    private static string? Violation(IContent content, IContent? stored, string statusAlias, string? archivedAlias)
    {
        if (!LifecycleChangeScope.Is(LifecycleRoutes.Archive))
        {
            if (!string.IsNullOrWhiteSpace(archivedAlias) && content.HasProperty(archivedAlias)
                && !string.Equals(Text(content.GetValue(archivedAlias)), stored is null ? null : Text(stored.GetValue(archivedAlias)), StringComparison.Ordinal))
            {
                return GateMessages.LifecycleArchiveRequiresRoute;
            }
        }

        if (!content.HasProperty(statusAlias))
        {
            return null;
        }

        var before = stored is null ? null : LifecycleStatuses.Of(stored, statusAlias);
        var after = LifecycleStatuses.Of(content, statusAlias);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return null;
        }

        if ((after == LifecycleStatuses.Archived || before == LifecycleStatuses.Archived) && !LifecycleChangeScope.Is(LifecycleRoutes.Archive))
        {
            return GateMessages.LifecycleArchiveRequiresRoute;
        }

        return after == LifecycleStatuses.Rejected && !LifecycleChangeScope.Is(LifecycleRoutes.Rejection)
            ? GateMessages.LifecycleRejectionRequiresDecision
            : null;
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
        ? text.Trim()
        : null;
}
