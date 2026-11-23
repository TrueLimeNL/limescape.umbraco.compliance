using System.Globalization;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Audit handlers for documents and media. Every route (backoffice, Management API, the Agent API,
/// jobs, code) goes through <see cref="IContentService"/> or <see cref="IMediaService"/>, so these notifications are the
/// one source of audit entries for content: nothing else writes create, update, publish, unpublish, move, trash,
/// restore or delete entries. The person comes from <see cref="ActingUser.IActingUserAccessor"/>.
/// <list type="bullet">
/// <item>Save: the "saving" notification takes a snapshot of the stored values, the "saved" notification writes one
/// <see cref="AuditActions.Update"/> entry per changed field (old and new value, truncated to 2,000 characters). A new
/// item gets one <see cref="AuditActions.Create"/> entry for the item and one per filled field.</item>
/// <item>Publish, unpublish and move (parent changed): one entry per item.</item>
/// <item>Relations between documents (typed links): one <see cref="AuditActions.Relate"/> or
/// <see cref="AuditActions.Unrelate"/> entry on each of the two documents, field <c>@relation.&lt;alias&gt;</c>, value
/// <c>{"parent":…,"child":…}</c>. Umbraco's own relation types (reference tracking, copy, delete) are not audited.</item>
/// <item>Trash, restore and delete also write the deletion log; a delete inside a <see cref="PurgeScope"/> is logged as
/// purged. The deletion log records the retention class, read before <see cref="RetentionRecalculationHandler"/>
/// forgets the item's retention, so this handler must be registered before that one.</item>
/// </list>
/// A failing audit write is logged as an error and does not undo the operation, which has been committed already.
/// </summary>
public sealed partial class ContentAuditHandler(
    IAuditWriter auditWriter,
    IDeletionLogWriter deletionLogWriter,
    IContentService contentService,
    IMediaService mediaService,
    IRetentionService retentionService,
    IIdKeyMap idKeyMap,
    ILogger<ContentAuditHandler> logger)
    : INotificationHandler<ContentSavingNotification>,
      INotificationHandler<ContentSavedNotification>,
      INotificationHandler<ContentPublishedNotification>,
      INotificationHandler<ContentUnpublishedNotification>,
      INotificationHandler<ContentMovedNotification>,
      INotificationHandler<ContentMovedToRecycleBinNotification>,
      INotificationHandler<ContentDeletedNotification>,
      INotificationHandler<MediaSavingNotification>,
      INotificationHandler<MediaSavedNotification>,
      INotificationHandler<MediaMovedNotification>,
      INotificationHandler<MediaMovedToRecycleBinNotification>,
      INotificationHandler<MediaDeletedNotification>,
      INotificationHandler<RelationSavedNotification>,
      INotificationHandler<RelationDeletedNotification>
{
    private const string BeforeStateKey = CompliancePackage.Id + ".Audit.Before";

    /// <summary>Field of a move entry: the parent before and after.</summary>
    public const string ParentField = "@parent";

    /// <summary>Prefix of the field of a relation entry; the relation type alias follows.</summary>
    public const string RelationFieldPrefix = "@relation.";

    /// <summary>Relation types Umbraco maintains itself; they are not changes a person makes to a material.</summary>
    private static readonly HashSet<string> SystemRelationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        Constants.Conventions.RelationTypes.RelateDocumentOnCopyAlias,
        Constants.Conventions.RelationTypes.RelateParentDocumentOnDeleteAlias,
        Constants.Conventions.RelationTypes.RelateParentMediaFolderOnDeleteAlias,
        Constants.Conventions.RelationTypes.RelatedDocumentAlias,
        Constants.Conventions.RelationTypes.RelatedMediaAlias,
        Constants.Conventions.RelationTypes.RelatedMemberAlias,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void Handle(ContentSavingNotification notification) =>
        RememberBefore(notification, notification.SavedEntities.Where(c => !c.Blueprint), id => contentService.GetById(id));

    /// <inheritdoc />
    public void Handle(ContentSavedNotification notification) =>
        WriteSaves(notification, notification.SavedEntities.Where(c => !c.Blueprint), AuditEntityTypes.Document);

    /// <inheritdoc />
    public void Handle(ContentPublishedNotification notification) =>
        Safely(() =>
        {
            foreach (var content in notification.PublishedEntities.Where(c => !c.Blueprint))
            {
                auditWriter.Write(new AuditRecord(AuditActions.Publish, AuditEntityTypes.Document, content.Key) { NewValue = Cultures(content) });
            }
        });

    /// <inheritdoc />
    public void Handle(ContentUnpublishedNotification notification) =>
        Safely(() =>
        {
            foreach (var content in notification.UnpublishedEntities.Where(c => !c.Blueprint))
            {
                auditWriter.Write(new AuditRecord(AuditActions.Unpublish, AuditEntityTypes.Document, content.Key));
            }
        });

    /// <inheritdoc />
    public void Handle(ContentMovedNotification notification) =>
        WriteMoves(notification.MoveInfoCollection, AuditEntityTypes.Document, Constants.System.RecycleBinContent, UmbracoObjectTypes.Document);

    /// <inheritdoc />
    public void Handle(ContentMovedToRecycleBinNotification notification) =>
        WriteTrash(notification.MoveInfoCollection.Select(m => m.Entity), AuditEntityTypes.Document, Constants.System.RecycleBinContent);

    /// <inheritdoc />
    public void Handle(ContentDeletedNotification notification) =>
        WriteDeletes(notification.DeletedEntities, AuditEntityTypes.Document);

    /// <inheritdoc />
    public void Handle(MediaSavingNotification notification) =>
        RememberBefore(notification, notification.SavedEntities, id => mediaService.GetById(id));

    /// <inheritdoc />
    public void Handle(MediaSavedNotification notification) =>
        WriteSaves(notification, notification.SavedEntities, AuditEntityTypes.Media);

    /// <inheritdoc />
    public void Handle(MediaMovedNotification notification) =>
        WriteMoves(notification.MoveInfoCollection, AuditEntityTypes.Media, Constants.System.RecycleBinMedia, UmbracoObjectTypes.Media);

    /// <inheritdoc />
    public void Handle(MediaMovedToRecycleBinNotification notification) =>
        WriteTrash(notification.MoveInfoCollection.Select(m => m.Entity), AuditEntityTypes.Media, Constants.System.RecycleBinMedia);

    /// <inheritdoc />
    public void Handle(MediaDeletedNotification notification) =>
        WriteDeletes(notification.DeletedEntities, AuditEntityTypes.Media);

    /// <inheritdoc />
    public void Handle(RelationSavedNotification notification) =>
        WriteRelations(notification.SavedEntities, AuditActions.Relate);

    /// <inheritdoc />
    public void Handle(RelationDeletedNotification notification) =>
        WriteRelations(notification.DeletedEntities, AuditActions.Unrelate);

    /// <summary>Stores the snapshot of the stored item (null for a new item) in the notification state, which Umbraco hands to the saved notification.</summary>
    private void RememberBefore<T>(StatefulNotification notification, IEnumerable<T> items, Func<int, IContentBase?> load)
        where T : IContentBase
    {
        try
        {
            var before = new Dictionary<Guid, ContentSnapshot?>();
            foreach (var item in items)
            {
                // The services return a copy from the cache or the database, so it still holds the stored values.
                before[item.Key] = item.HasIdentity && load(item.Id) is { } stored ? ContentSnapshot.Of(stored) : null;
            }

            notification.State[BeforeStateKey] = before;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex, "snapshot");
        }
    }

    private void WriteSaves<T>(StatefulNotification notification, IEnumerable<T> items, string entityType)
        where T : IContentBase =>
        Safely(() =>
        {
            if (notification.State.TryGetValue(BeforeStateKey, out var state) is false || state is not Dictionary<Guid, ContentSnapshot?> before)
            {
                LogNoSnapshot(logger, entityType);
                return;
            }

            foreach (var item in items)
            {
                if (!before.TryGetValue(item.Key, out var snapshot))
                {
                    continue;
                }

                var after = ContentSnapshot.Of(item);
                if (snapshot is null)
                {
                    auditWriter.Write(new AuditRecord(AuditActions.Create, entityType, item.Key)
                    {
                        NewValue = JsonSerializer.Serialize(new { contentType = item.ContentType.Alias, parent = ParentKey(item.ParentId, entityType) }, Json),
                    });
                }

                foreach (var change in ContentSnapshot.Diff(snapshot ?? ContentSnapshot.Empty, after))
                {
                    auditWriter.Write(new AuditRecord(snapshot is null ? AuditActions.Create : AuditActions.Update, entityType, item.Key)
                    {
                        Field = change.Field,
                        OldValue = change.OldValue,
                        NewValue = change.NewValue,
                    });
                }
            }
        });

    private void WriteMoves<T>(IEnumerable<MoveEventInfo<T>> moves, string entityType, int recycleBinId, UmbracoObjectTypes objectType)
        where T : IContentBase =>
        Safely(() =>
        {
            foreach (var move in moves)
            {
                // Descendants move along with their parent; only the item whose parent changed is the move.
                var oldParentId = ParentIdFromPath(move.OriginalPath);
                if (oldParentId is null || oldParentId == move.Entity.ParentId)
                {
                    continue;
                }

                var item = move.Entity;
                if (oldParentId == recycleBinId)
                {
                    auditWriter.Write(new AuditRecord(AuditActions.Restore, entityType, item.Key) { NewValue = move.NewParentKey?.ToString() ?? "root" });
                    deletionLogWriter.Write(new DeletionLogRecord(DeletionLogEvents.Restored, entityType, item.Key)
                    {
                        TitleSnapshot = item.Name,
                        RetentionClass = RetentionClassOf(item.Key),
                    });
                    continue;
                }

                auditWriter.Write(new AuditRecord(AuditActions.Move, entityType, item.Key)
                {
                    Field = ParentField,
                    OldValue = KeyOf(oldParentId.Value, objectType),
                    NewValue = move.NewParentKey?.ToString() ?? "root",
                });
            }
        });

    private void WriteTrash(IEnumerable<IContentBase> items, string entityType, int recycleBinId) =>
        Safely(() =>
        {
            // Descendants are moved along; the trashed item is the one placed directly in the recycle bin.
            foreach (var item in items.Where(i => i.ParentId == recycleBinId))
            {
                auditWriter.Write(new AuditRecord(AuditActions.Trash, entityType, item.Key));
                deletionLogWriter.Write(new DeletionLogRecord(DeletionLogEvents.Trashed, entityType, item.Key)
                {
                    TitleSnapshot = item.Name,
                    RetentionClass = RetentionClassOf(item.Key),
                });
            }
        });

    private void WriteDeletes(IEnumerable<IContentBase> items, string entityType) =>
        Safely(() =>
        {
            var purge = PurgeScope.Current;
            foreach (var item in items)
            {
                auditWriter.Write(new AuditRecord(AuditActions.Delete, entityType, item.Key) { OldValue = item.Name, NewValue = purge?.Reason });
                deletionLogWriter.Write(new DeletionLogRecord(purge is null ? DeletionLogEvents.Deleted : DeletionLogEvents.Purged, entityType, item.Key)
                {
                    TitleSnapshot = item.Name,
                    Reason = purge?.Reason,
                    RetentionClass = RetentionClassOf(item.Key),
                });
            }
        });

    private void WriteRelations(IEnumerable<IRelation> relations, string action) =>
        Safely(() =>
        {
            foreach (var relation in relations)
            {
                if (SystemRelationTypes.Contains(relation.RelationType.Alias)
                    || relation.RelationType is IRelationTypeWithIsDependency { IsDependency: true }
                    || relation.RelationType.ParentObjectType != Constants.ObjectTypes.Document
                    || relation.RelationType.ChildObjectType != Constants.ObjectTypes.Document)
                {
                    continue;
                }

                var parent = idKeyMap.GetKeyForId(relation.ParentId, UmbracoObjectTypes.Document);
                var child = idKeyMap.GetKeyForId(relation.ChildId, UmbracoObjectTypes.Document);
                if (!parent.Success || !child.Success)
                {
                    continue;
                }

                // Both materials get the entry, so the history of either one shows the link.
                var value = JsonSerializer.Serialize(new { parent = parent.Result, child = child.Result }, Json);
                foreach (var key in new[] { parent.Result, child.Result })
                {
                    auditWriter.Write(new AuditRecord(action, AuditEntityTypes.Document, key)
                    {
                        Field = RelationFieldPrefix + relation.RelationType.Alias,
                        OldValue = action == AuditActions.Unrelate ? value : null,
                        NewValue = action == AuditActions.Relate ? value : null,
                    });
                }
            }
        });

    /// <summary>Name of the governing retention class (or all classes when none governs), from the stored retention.</summary>
    private string? RetentionClassOf(Guid key)
    {
        var info = retentionService.GetItem(key);
        if (info is null || info.Rules.Count == 0)
        {
            return null;
        }

        return info.Rules.FirstOrDefault(r => r.Governing) is { } governing
            ? governing.ClassName
            : string.Join(", ", info.Rules.Select(r => r.ClassName).Distinct(StringComparer.Ordinal));
    }

    private string? ParentKey(int parentId, string entityType) =>
        KeyOf(parentId, entityType == AuditEntityTypes.Media ? UmbracoObjectTypes.Media : UmbracoObjectTypes.Document);

    private string? KeyOf(int id, UmbracoObjectTypes objectType)
    {
        if (id == Constants.System.Root)
        {
            return "root";
        }

        var attempt = idKeyMap.GetKeyForId(id, objectType);
        return attempt.Success ? attempt.Result.ToString() : id.ToString(CultureInfo.InvariantCulture);
    }

    private static int? ParentIdFromPath(string? path)
    {
        var parts = path?.Split(',') ?? [];
        return parts.Length >= 2 && int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static string? Cultures(IContent content) =>
        content.ContentType.VariesByCulture() ? string.Join(",", content.PublishedCultures.Order(StringComparer.Ordinal)) : null;

    private void Safely(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex, "write");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Compliance audit {Step} failed; the operation itself has completed")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance audit: a {EntityType} was saved without a snapshot of its stored values; no field-level entries written")]
    private static partial void LogNoSnapshot(ILogger logger, string entityType);
}
