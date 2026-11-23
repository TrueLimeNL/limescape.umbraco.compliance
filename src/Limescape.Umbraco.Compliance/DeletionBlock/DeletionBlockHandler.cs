using System.Globalization;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.DeletionBlock;

/// <summary>
/// Deletion block. Cancels every service-level route by which a document or media item leaves
/// the tree or the database while it is under retention or legal hold: moving to the recycle bin, deleting (also from
/// the recycle bin and as part of a bulk or scheduled operation), emptying the recycle bin and deleting a document or
/// media type. The backoffice, the Management API and background jobs all go through these services, so no route
/// bypasses the block. Each refusal gets a localized message, an audit entry and, for API callers, the error code
/// <see cref="DeletionBlockErrorCodes.RetentionBlocksDeletion"/> (see <see cref="DeletionBlockResultFilter"/>).
/// Emptying the recycle bin removes the items that may go and keeps the rest.
/// </summary>
public sealed class DeletionBlockHandler(
    IDeletionBlockService deletionBlockService,
    IAuditWriter auditWriter,
    IContentService contentService,
    IMediaService mediaService,
    IContentTypeService contentTypeService,
    IMediaTypeService mediaTypeService,
    IRelationService relationService,
    IOptionsMonitor<ContentSettings> contentSettings,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    IHttpContextAccessor httpContextAccessor)
    : INotificationHandler<ContentMovingToRecycleBinNotification>,
      INotificationHandler<ContentDeletingNotification>,
      INotificationHandler<ContentEmptyingRecycleBinNotification>,
      INotificationHandler<MediaMovingToRecycleBinNotification>,
      INotificationHandler<MediaDeletingNotification>,
      INotificationHandler<MediaEmptyingRecycleBinNotification>,
      INotificationHandler<ContentTypeDeletingNotification>,
      INotificationHandler<MediaTypeDeletingNotification>
{
    /// <inheritdoc />
    public void Handle(ContentMovingToRecycleBinNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Refuse(notification, notification.Messages, deletionBlockService.Check(notification.MoveInfoCollection.Select(m => m.Entity)), DeletionBlockOperation.Trash);
    }

    /// <inheritdoc />
    public void Handle(ContentDeletingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Refuse(notification, notification.Messages, deletionBlockService.Check(notification.DeletedEntities), DeletionBlockOperation.Delete);
    }

    /// <inheritdoc />
    public void Handle(ContentEmptyingRecycleBinNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var settings = contentSettings.CurrentValue;
        int[]? excludeRelationTypeIds = null;
        if (settings.DisableDeleteWhenReferenced
            && relationService.GetRelationTypeByAlias(Constants.Conventions.RelationTypes.RelateParentDocumentOnDeleteAlias) is { } parentRelation)
        {
            excludeRelationTypeIds = [parentRelation.Id];
        }

        EmptyPartially(
            notification,
            notification.Messages,
            notification.DeletedEntities?.ToList() ?? [],
            item =>
            {
                // Same rule as ContentService.EmptyRecycleBin: referenced items stay when deleting referenced items is disabled.
                if (settings.DisableDeleteWhenReferenced && relationService.IsRelated(item.Id, RelationDirectionFilter.Child, null, excludeRelationTypeIds))
                {
                    return false;
                }

                return contentService.Delete(item, CurrentUserId()).Success;
            });
    }

    /// <inheritdoc />
    public void Handle(MediaMovingToRecycleBinNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Refuse(notification, notification.Messages, deletionBlockService.Check(notification.MoveInfoCollection.Select(m => m.Entity)), DeletionBlockOperation.Trash);
    }

    /// <inheritdoc />
    public void Handle(MediaDeletingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Refuse(notification, notification.Messages, deletionBlockService.Check(notification.DeletedEntities), DeletionBlockOperation.Delete);
    }

    /// <inheritdoc />
    public void Handle(MediaEmptyingRecycleBinNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        EmptyPartially(notification, notification.Messages, notification.DeletedEntities?.ToList() ?? [], item => mediaService.Delete(item, CurrentUserId()).Success);
    }

    /// <inheritdoc />
    public void Handle(ContentTypeDeletingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var typeIds = notification.DeletedEntities.SelectMany(t => contentTypeService.GetDescendants(t.Id, andSelf: true)).Select(t => t.Id);
        Refuse(notification, notification.Messages, deletionBlockService.CheckTypes(typeIds), DeletionBlockOperation.DeleteType);
    }

    /// <inheritdoc />
    public void Handle(MediaTypeDeletingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var typeIds = notification.DeletedEntities.SelectMany(t => mediaTypeService.GetDescendants(t.Id, andSelf: true)).Select(t => t.Id);
        Refuse(notification, notification.Messages, deletionBlockService.CheckTypes(typeIds), DeletionBlockOperation.DeleteType);
    }

    /// <summary>
    /// Deletes the top-level recycle-bin items that are not blocked (themselves or through a descendant) one by one, then
    /// cancels the bulk operation so the blocked items stay. Each single delete passes the deletion block again.
    /// </summary>
    private void EmptyPartially<TItem>(ICancelableNotification notification, EventMessages messages, IReadOnlyList<TItem> items, Func<TItem, bool> delete)
        where TItem : IContentBase
    {
        var blocks = deletionBlockService.Check(items.Cast<IContentBase>());
        if (blocks.Count == 0)
        {
            return;
        }

        var blockedTopLevel = blocks.Select(b => b.ViaItemKey ?? b.ItemKey).ToHashSet();
        var deleted = items.Where(i => !blockedTopLevel.Contains(i.Key)).Count(delete);
        var culture = Culture();
        Refuse(notification, messages, blocks, DeletionBlockOperation.EmptyRecycleBin, DeletionBlockMessages.PartialEmpty(deleted, items.Count - deleted, culture));
    }

    private void Refuse(ICancelableNotification notification, EventMessages messages, IReadOnlyList<DeletionBlock> blocks, DeletionBlockOperation operation, string? summary = null)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        var culture = Culture();
        var category = DeletionBlockMessages.Category(culture);
        var lines = DeletionBlockMessages.Lines(blocks, culture);
        if (summary is not null)
        {
            lines = [summary, .. lines];
        }

        notification.Cancel = true;
        foreach (var line in lines)
        {
            messages.Add(new EventMessage(category, line, EventMessageType.Error));
        }

        foreach (var block in blocks)
        {
            auditWriter.Write(new AuditRecord(AuditActions.DeleteBlocked, block.EntityType, block.ItemKey)
            {
                Field = RetentionService.Storage(operation),
                NewValue = DeletionBlockMessages.Invariant(block),
            });
        }

        if (httpContextAccessor.HttpContext is { } httpContext)
        {
            DeletionBlockRequestState.Add(httpContext, blocks, lines, culture);
        }
    }

    // -1 is the services' default user id (the super user), used when no backoffice user is known, for example in a job.
    private int CurrentUserId() => backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Id ?? -1;

    /// <summary>
    /// The language of the message: for an API user the request's Accept-Language (an integration acts for people with
    /// different languages), otherwise the backoffice user's language; English when neither is Dutch or English.
    /// </summary>
    private CultureInfo Culture()
    {
        var user = backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null or { Kind: UserKind.Api }
            && httpContextAccessor.HttpContext?.Request.GetTypedHeaders().AcceptLanguage is { Count: > 0 } accepted)
        {
            foreach (var language in accepted.OrderByDescending(l => l.Quality ?? 1))
            {
                if (Supported(language.Value.Value) is { } culture)
                {
                    return culture;
                }
            }
        }

        return Supported(user?.Language) ?? CultureInfo.GetCultureInfo("en");
    }

    private static CultureInfo? Supported(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            return culture.TwoLetterISOLanguageName is "nl" or "en" ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
