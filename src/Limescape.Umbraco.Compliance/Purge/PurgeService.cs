using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.Retention;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Purge;

/// <summary>
/// Permanent deletion of items that have been in the recycle bin longer than a restoration period.
/// Items under retention or legal hold (themselves or through a descendant) are skipped and the skip is audited; the
/// others are deleted through <see cref="IContentService"/> and <see cref="IMediaService"/>, so the deletion block
/// checks them once more, and the deletion log records them as purged with the reason and retention class.
/// </summary>
public interface IPurgeService
{
    /// <summary>
    /// Purges the top-level recycle-bin items trashed at least <paramref name="restorationPeriod"/> ago.
    /// With <paramref name="dryRun"/> nothing is deleted or logged; the result lists what would happen.
    /// Must not be called inside an open Umbraco scope (the deletion log would not see the purge).
    /// </summary>
    PurgeResult Purge(TimeSpan restorationPeriod, bool dryRun, IReadOnlyCollection<string> entityTypes, int userId = -1);
}

/// <summary>Outcome of a purge.</summary>
/// <param name="CutoffUtc">Items trashed before this moment were due.</param>
/// <param name="DryRun">True when nothing was deleted.</param>
/// <param name="Purged">Items deleted (or, in a dry run, that would be deleted).</param>
/// <param name="Skipped">Due items that stay in the recycle bin, with the reason.</param>
/// <param name="NotDue">Number of recycle-bin items trashed after the cutoff.</param>
/// <param name="Elements">What happened to the elements (media) of the purged documents.</param>
public sealed record PurgeResult(DateTime CutoffUtc, bool DryRun, IReadOnlyList<PurgeItem> Purged, IReadOnlyList<PurgeSkippedItem> Skipped, int NotDue, IReadOnlyList<PurgeElement> Elements);

/// <summary>An element (media item) of a purged document.</summary>
/// <param name="Key">The media item.</param>
/// <param name="MaterialKey">The purged document that picked it.</param>
/// <param name="Name">Name of the media item.</param>
/// <param name="Purged">True when it was deleted (in a dry run: would be); false when it was skipped.</param>
/// <param name="Reason">For a skipped element one of <see cref="PurgeElementReasons"/>.</param>
/// <param name="BlockedItems">The items under retention or legal hold that kept the element, for <see cref="PurgeElementReasons.RetentionBlocksDeletion"/>.</param>
public sealed record PurgeElement(Guid Key, Guid MaterialKey, string Name, bool Purged, string? Reason, IReadOnlyList<DeletionBlock.DeletionBlock> BlockedItems);

/// <summary>Why an element of a purged document was kept.</summary>
public static class PurgeElementReasons
{
    /// <summary>Another item still uses the element.</summary>
    public const string Shared = "elementShared";

    /// <summary>The element (or a descendant) is under retention or legal hold.</summary>
    public const string RetentionBlocksDeletion = DeletionBlockErrorCodes.RetentionBlocksDeletion;

    /// <summary>The delete was cancelled by another rule.</summary>
    public const string Cancelled = PurgeSkipReasons.Cancelled;
}

/// <summary>A purged item.</summary>
public sealed record PurgeItem(Guid Key, string EntityType, string Name, DateTime TrashedUtc);

/// <summary>A due item that was not purged.</summary>
/// <param name="Key">The recycle-bin item.</param>
/// <param name="EntityType">document or media.</param>
/// <param name="Name">Name of the item.</param>
/// <param name="TrashedUtc">When it was moved to the recycle bin.</param>
/// <param name="Reason"><see cref="PurgeSkipReasons.RetentionBlocksDeletion"/> or <see cref="PurgeSkipReasons.Cancelled"/>.</param>
/// <param name="BlockedItems">The items (the item itself or descendants) under retention or legal hold.</param>
/// <param name="Message">Message of the operation that refused the delete, if any.</param>
public sealed record PurgeSkippedItem(Guid Key, string EntityType, string Name, DateTime TrashedUtc, string Reason, IReadOnlyList<DeletionBlock.DeletionBlock> BlockedItems, string? Message);

/// <summary>Why a due item was skipped.</summary>
public static class PurgeSkipReasons
{
    /// <summary>The item or a descendant is under retention or legal hold.</summary>
    public const string RetentionBlocksDeletion = DeletionBlockErrorCodes.RetentionBlocksDeletion;

    /// <summary>The delete was cancelled by another rule.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Default <see cref="IPurgeService"/>.
/// </summary>
public sealed class PurgeService(
    IContentService contentService,
    IMediaService mediaService,
    IDeletionBlockService deletionBlockService,
    IAuditLogReader auditLogReader,
    IAuditWriter auditWriter,
    IDeletionLogWriter deletionLogWriter,
    IMaterialElements materialElements,
    TimeProvider timeProvider) : IPurgeService
{
    private const int PageSize = 500;

    /// <inheritdoc />
    public PurgeResult Purge(TimeSpan restorationPeriod, bool dryRun, IReadOnlyCollection<string> entityTypes, int userId = -1)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        if (restorationPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(restorationPeriod), "The restoration period cannot be negative.");
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - restorationPeriod;
        var purged = new List<PurgeItem>();
        var skipped = new List<PurgeSkippedItem>();
        var elements = new List<PurgeElement>();
        var notDue = 0;

        var items = new List<IContentBase>();
        if (entityTypes.Contains(AuditEntityTypes.Document))
        {
            items.AddRange(Children((long page, out long total) => contentService.GetPagedChildren(Constants.System.RecycleBinContent, page, PageSize, out total, propertyAliases: null, filter: null, ordering: null)));
        }

        if (entityTypes.Contains(AuditEntityTypes.Media))
        {
            items.AddRange(Children((long page, out long total) => mediaService.GetPagedChildren(Constants.System.RecycleBinMedia, page, PageSize, out total)));
        }

        foreach (var item in items)
        {
            var entityType = item is IMedia ? AuditEntityTypes.Media : AuditEntityTypes.Document;
            var trashed = auditLogReader.LastTrashedUtc(item.Key) ?? DateTime.SpecifyKind(item.UpdateDate, DateTimeKind.Utc);
            if (trashed > cutoff)
            {
                notDue++;
                continue;
            }

            var name = item.Name ?? string.Empty;
            var blocks = deletionBlockService.Check([item]);
            if (blocks.Count > 0)
            {
                skipped.Add(new PurgeSkippedItem(item.Key, entityType, name, trashed, PurgeSkipReasons.RetentionBlocksDeletion, blocks, null));
                if (!dryRun)
                {
                    foreach (var block in blocks)
                    {
                        auditWriter.Write(new AuditRecord(AuditActions.DeleteBlocked, block.EntityType, block.ItemKey)
                        {
                            Field = RetentionService.Storage(DeletionBlockOperation.Purge),
                            NewValue = DeletionBlockMessages.Invariant(block),
                        });
                    }
                }

                continue;
            }

            if (dryRun)
            {
                purged.Add(new PurgeItem(item.Key, entityType, name, trashed));
                if (item is IContent dryContent)
                {
                    elements.AddRange(PurgeElements(Owned(dryContent), dryRun: true, userId));
                }

                continue;
            }

            // The elements and their users are read before the delete: it removes the document and its references.
            var toTake = item is IContent content ? Owned(content) : null;

            OperationResult result;
            using (PurgeScope.Begin(PurgeReasons.RestorationPeriodEnded))
            {
                result = item is IMedia media ? mediaService.Delete(media, userId).Result! : contentService.Delete((IContent)item, userId);
            }

            if (result.Success)
            {
                purged.Add(new PurgeItem(item.Key, entityType, name, trashed));
                if (toTake is not null)
                {
                    elements.AddRange(PurgeElements(toTake, dryRun: false, userId));
                }
            }
            else
            {
                // The deletion block (or another rule) refused the delete after all; it has audited its own refusal.
                var message = string.Join(" ", result.EventMessages?.GetAll().Select(m => m.Message) ?? []);
                skipped.Add(new PurgeSkippedItem(item.Key, entityType, name, trashed, PurgeSkipReasons.Cancelled, [], message.Length == 0 ? null : message));
            }
        }

        return new PurgeResult(cutoff, dryRun, purged, skipped, notDue, elements);
    }

    /// <summary>The elements of a document and of its descendants, with the ids of all those documents (they do not count as other users).</summary>
    private OwnedElements Owned(IContent root)
    {
        var owners = new List<IContent> { root };
        long page = 0;
        long total;
        do
        {
            owners.AddRange(contentService.GetPagedDescendants(root.Id, page++, PageSize, out total));
        }
        while (page * PageSize < total);

        var keys = owners.SelectMany(o => materialElements.KeysOf(o).Select(k => (Key: k, Owner: o.Key))).ToList();
        return new OwnedElements(keys, owners.Select(o => o.Id).ToHashSet());
    }

    private List<PurgeElement> PurgeElements(OwnedElements owned, bool dryRun, int userId)
    {
        var results = new List<PurgeElement>();
        foreach (var (mediaKey, materialKey) in owned.Elements.DistinctBy(e => e.Key))
        {
            if (mediaService.GetById(mediaKey) is not { } media)
            {
                continue;
            }

            var name = media.Name ?? string.Empty;
            if (materialElements.IsUsedElsewhere(media, owned.OwnerIds, ignoreTrashedUsers: false))
            {
                results.Add(new PurgeElement(mediaKey, materialKey, name, false, PurgeElementReasons.Shared, []));
                LogSkip(media, materialKey, PurgeElementReasons.Shared, dryRun, []);
                continue;
            }

            var blocks = deletionBlockService.Check([media]);
            if (blocks.Count > 0)
            {
                results.Add(new PurgeElement(mediaKey, materialKey, name, false, PurgeElementReasons.RetentionBlocksDeletion, blocks));
                LogSkip(media, materialKey, PurgeElementReasons.RetentionBlocksDeletion, dryRun, blocks);
                continue;
            }

            if (dryRun)
            {
                results.Add(new PurgeElement(mediaKey, materialKey, name, true, null, []));
                continue;
            }

            OperationResult? deleted;
            using (PurgeScope.Begin(PurgeReasons.ElementOf(materialKey)))
            {
                deleted = mediaService.Delete(media, userId).Result;
            }

            results.Add(deleted?.Success == true
                ? new PurgeElement(mediaKey, materialKey, name, true, null, [])
                : new PurgeElement(mediaKey, materialKey, name, false, PurgeElementReasons.Cancelled, []));
        }

        return results;
    }

    /// <summary>
    /// A skipped element gets a deletion-log row of its own (event <see cref="DeletionLogEvents.PurgeSkipped"/>, reason
    /// <c>{reason}:{material key}</c>); a block by retention or legal hold is audited like any other refused delete.
    /// </summary>
    private void LogSkip(IMedia media, Guid materialKey, string reason, bool dryRun, IReadOnlyList<DeletionBlock.DeletionBlock> blocks)
    {
        if (dryRun)
        {
            return;
        }

        foreach (var block in blocks)
        {
            auditWriter.Write(new AuditRecord(AuditActions.DeleteBlocked, block.EntityType, block.ItemKey)
            {
                Field = RetentionService.Storage(DeletionBlockOperation.Purge),
                NewValue = DeletionBlockMessages.Invariant(block),
            });
        }

        deletionLogWriter.Write(new DeletionLogRecord(DeletionLogEvents.PurgeSkipped, AuditEntityTypes.Media, media.Key)
        {
            TitleSnapshot = media.Name,
            Reason = PurgeReasons.ElementOf(materialKey, reason),
        });
    }

    private sealed record OwnedElements(IReadOnlyList<(Guid Key, Guid Material)> Elements, IReadOnlySet<int> OwnerIds);

    private delegate IEnumerable<IContentBase> PageLoader(long pageIndex, out long total);

    private static List<IContentBase> Children(PageLoader load)
    {
        var all = new List<IContentBase>();
        long page = 0;
        long total;
        do
        {
            all.AddRange(load(page++, out total));
        }
        while (page * PageSize < total);

        return all;
    }
}
