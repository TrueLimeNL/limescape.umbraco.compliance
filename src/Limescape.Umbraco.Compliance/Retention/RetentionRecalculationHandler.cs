using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;

namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Recalculates retention when an item is saved (which covers archiving: the archive date is a property),
/// published or deleted. Event-driven recalculation never shortens a stored period.
/// </summary>
public sealed partial class RetentionRecalculationHandler(IRetentionService retentionService, ILogger<RetentionRecalculationHandler> logger)
    : INotificationHandler<ContentSavedNotification>,
      INotificationHandler<ContentPublishedNotification>,
      INotificationHandler<MediaSavedNotification>,
      INotificationHandler<ContentDeletedNotification>,
      INotificationHandler<MediaDeletedNotification>
{
    /// <inheritdoc />
    public void Handle(ContentSavedNotification notification) => Recalculate(notification.SavedEntities);

    /// <inheritdoc />
    public void Handle(ContentPublishedNotification notification) => Recalculate(notification.PublishedEntities);

    /// <inheritdoc />
    public void Handle(MediaSavedNotification notification) => Recalculate(notification.SavedEntities);

    /// <inheritdoc />
    public void Handle(ContentDeletedNotification notification) => Forget(notification.DeletedEntities);

    /// <inheritdoc />
    public void Handle(MediaDeletedNotification notification) => Forget(notification.DeletedEntities);

    private void Recalculate(IEnumerable<IContentBase> items)
    {
        foreach (var item in items)
        {
            if (item is IContent { Blueprint: true })
            {
                continue;
            }

            try
            {
                retentionService.Recalculate(item);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The operation itself has completed; a failed recalculation is retried on the next save or by an
                // administrator (recalculate endpoint). Never let it break the editor's save.
                LogRecalculationFailed(logger, ex, item.Key);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention recalculation failed for {ItemKey}")]
    private static partial void LogRecalculationFailed(ILogger logger, Exception exception, Guid itemKey);

    private void Forget(IEnumerable<IContentBase> items)
    {
        foreach (var item in items)
        {
            retentionService.Forget(item.Key);
        }
    }
}
