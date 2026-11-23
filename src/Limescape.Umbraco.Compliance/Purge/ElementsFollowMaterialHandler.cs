using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Purge;

/// <summary>
/// With <see cref="PurgeOptions.ElementsFollowMaterial"/> (default), the elements of a document go to the media recycle bin
/// when the document does and come back when it is restored. An element that another document in the tree still uses stays
/// where it is. The media moves go through <see cref="IMediaService"/>, so the audit handlers write a trash or restore entry
/// per element and the deletion block refuses to trash an element under retention or legal hold (the element then stays
/// where it is and the refusal is audited by the block).
/// </summary>
public sealed partial class ElementsFollowMaterialHandler(
    IMaterialElements elements,
    IMediaService mediaService,
    IRelationService relationService,
    IEntityService entityService,
    IOptionsMonitor<PurgeOptions> options,
    ILogger<ElementsFollowMaterialHandler> logger)
    : INotificationHandler<ContentMovedToRecycleBinNotification>,
      INotificationHandler<ContentMovedNotification>
{
    private const int SystemUser = -1;

    /// <inheritdoc />
    public void Handle(ContentMovedToRecycleBinNotification notification)
    {
        if (!options.CurrentValue.ElementsFollowMaterial)
        {
            return;
        }

        // Descendants are moved along with the item, and an element may be shared between them.
        var documents = notification.MoveInfoCollection.Select(m => m.Entity).ToList();
        var ownerIds = documents.Select(d => d.Id).ToHashSet();
        foreach (var key in documents.SelectMany(elements.KeysOf).Distinct())
        {
            Safely(key, () =>
            {
                if (mediaService.GetById(key) is not { Trashed: false } media || elements.IsUsedElsewhere(media, ownerIds, ignoreTrashedUsers: true))
                {
                    return;
                }

                if (!mediaService.MoveToRecycleBin(media, SystemUser).Success)
                {
                    LogElementStays(logger, key);
                }
            });
        }
    }

    /// <inheritdoc />
    public void Handle(ContentMovedNotification notification)
    {
        if (!options.CurrentValue.ElementsFollowMaterial)
        {
            return;
        }

        var restored = notification.MoveInfoCollection
            .Where(m => !m.Entity.Trashed && WasInRecycleBin(m.OriginalPath))
            .Select(m => m.Entity)
            .ToList();
        foreach (var key in restored.SelectMany(elements.KeysOf).Distinct())
        {
            Safely(key, () =>
            {
                if (mediaService.GetById(key) is not { Trashed: true } media)
                {
                    return;
                }

                // Back to the folder it was in when it was trashed, or to the root when that folder is gone or trashed itself.
                var folderId = relationService.GetByChildId(media.Id, Constants.Conventions.RelationTypes.RelateParentMediaFolderOnDeleteAlias).FirstOrDefault()?.ParentId;
                var parentId = folderId is { } id && entityService.Get(id) is { Trashed: false } ? id : Constants.System.Root;
                if (!mediaService.Move(media, parentId, SystemUser).Success)
                {
                    LogElementStays(logger, key);
                }
            });
        }
    }

    private static bool WasInRecycleBin(string? originalPath) =>
        originalPath?.Split(',').Contains(Constants.System.RecycleBinContentString) == true;

    private void Safely(Guid mediaKey, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogElementFailed(logger, ex, mediaKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Element {MediaKey} of a trashed or restored document was not moved with it (refused by a rule)")]
    private static partial void LogElementStays(ILogger logger, Guid mediaKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Moving element {MediaKey} with its document failed; the document itself has been moved")]
    private static partial void LogElementFailed(ILogger logger, Exception exception, Guid mediaKey);
}
