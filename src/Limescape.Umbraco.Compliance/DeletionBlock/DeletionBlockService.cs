using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Persistence;
using Limescape.Umbraco.Compliance.Retention;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.DeletionBlock;

/// <summary>
/// Decides whether items may be deleted and sets the legal hold. An item blocks while it has a legal
/// hold, while its retention period runs until a date in the future, or while its period is open-ended (a class applies
/// whose start event has not happened yet). Trashing or deleting an item also deletes its descendants, so they are checked too.
/// </summary>
public interface IDeletionBlockService
{
    /// <summary>Blocks for <paramref name="items"/> and, when <paramref name="includeDescendants"/>, their descendants. Empty when deletion is allowed.</summary>
    IReadOnlyList<DeletionBlock> Check(IEnumerable<IContentBase> items, bool includeDescendants = true);

    /// <summary>Blocks for every item of the given document or media types and their descendants (deleting a type deletes its items).</summary>
    IReadOnlyList<DeletionBlock> CheckTypes(IEnumerable<int> contentTypeIds);

    /// <summary>
    /// Sets or lifts the legal hold of an item and writes it to the audit log. Callers must have checked that the
    /// acting user is an administrator. Returns the item's retention, or null when the item does not exist.
    /// </summary>
    ItemRetentionInfo? SetLegalHold(Guid itemKey, bool legalHold, string? reason);
}

/// <summary>
/// Default <see cref="IDeletionBlockService"/> on the <c>lsItemRetention</c> table.
/// </summary>
public sealed class DeletionBlockService(
    IScopeProvider scopeProvider,
    IRetentionService retentionService,
    IContentService contentService,
    IMediaService mediaService,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IDeletionBlockService
{
    /// <summary>SQL Server allows 2,100 parameters per statement; stay well below.</summary>
    private const int InChunk = 1000;

    /// <summary>The row blocks: legal hold, open-ended, or until in the future. Parameters: @1 = true, @2 = now (@0 is the query's own).</summary>
    private const string BlockedPredicate = "(r.legalHold = @1 OR r.openEnded = @1 OR r.retentionUntil > @2)";

    /// <inheritdoc />
    public IReadOnlyList<DeletionBlock> Check(IEnumerable<IContentBase> items, bool includeDescendants = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.Where(i => i is not IContent { Blueprint: true }).DistinctBy(i => i.Key).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var blocks = new List<DeletionBlock>();
        using var scope = scopeProvider.CreateScope(autoComplete: true);

        var rows = FetchRows(scope, list.Select(i => i.Key)).ToDictionary(r => r.ItemKey);
        foreach (var item in list)
        {
            if (rows.TryGetValue(item.Key, out var row))
            {
                if (ToBlock(row, item.Name ?? string.Empty, now) is { } block)
                {
                    blocks.Add(block);
                }
            }
            else if (FromCalculation(item) is { } calculated)
            {
                // Nothing stored yet (for example an item saved before the package was installed): calculate now.
                blocks.Add(calculated);
            }
        }

        if (includeDescendants)
        {
            var seen = blocks.Select(b => b.ItemKey).ToHashSet();
            foreach (var item in list.Where(i => !string.IsNullOrEmpty(i.Path)))
            {
                var keys = scope.Database.Fetch<Guid>(
                    scope.SqlContext.Sql(
                        $"SELECT r.itemKey FROM {ComplianceTables.ItemRetention} r INNER JOIN {Constants.DatabaseSchema.Tables.Node} n ON n.uniqueId = r.itemKey " +
                        "WHERE n.path LIKE @0 AND " + BlockedPredicate,
                        item.Path + ",%",
                        true,
                        now));
                foreach (var block in Blocks(scope, keys.Where(seen.Add), now))
                {
                    blocks.Add(block with { ViaItemKey = item.Key });
                }
            }
        }

        return blocks;
    }

    /// <inheritdoc />
    public IReadOnlyList<DeletionBlock> CheckTypes(IEnumerable<int> contentTypeIds)
    {
        ArgumentNullException.ThrowIfNull(contentTypeIds);
        var ids = contentTypeIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var descendantOf = scope.SqlContext.SqlSyntax.GetConcat("t.path", "',%'");
        var keys = scope.Database.Fetch<Guid>(
            scope.SqlContext.Sql(
                $"SELECT r.itemKey FROM {ComplianceTables.ItemRetention} r INNER JOIN {Constants.DatabaseSchema.Tables.Node} n ON n.uniqueId = r.itemKey " +
                "WHERE " + BlockedPredicate +
                $" AND EXISTS (SELECT 1 FROM {Constants.DatabaseSchema.Tables.Content} c INNER JOIN {Constants.DatabaseSchema.Tables.Node} t ON t.id = c.nodeId " +
                $"WHERE c.contentTypeId IN (@0) AND (t.id = n.id OR n.path LIKE {descendantOf}))",
                ids,
                true,
                now));
        return Blocks(scope, keys, now);
    }

    /// <inheritdoc />
    public ItemRetentionInfo? SetLegalHold(Guid itemKey, bool legalHold, string? reason)
    {
        IContentBase? item = (IContentBase?)contentService.GetById(itemKey) ?? mediaService.GetById(itemKey);
        if (item is null)
        {
            return null;
        }

        // Store the calculated retention first, so the row that carries the hold also carries the period.
        retentionService.Recalculate(item);

        using (var scope = scopeProvider.CreateScope())
        {
            var dto = FetchRows(scope, [itemKey]).SingleOrDefault();
            var before = dto?.LegalHold ?? false;
            if (dto is null)
            {
                dto = new ItemRetentionDto
                {
                    ItemKey = itemKey,
                    EntityType = item is IMedia ? AuditEntityTypes.Media : AuditEntityTypes.Document,
                    CalculatedUtc = timeProvider.GetUtcNow().UtcDateTime,
                    LegalHold = legalHold,
                };
                scope.Database.Insert(dto);
            }
            else if (before != legalHold)
            {
                dto.LegalHold = legalHold;
                scope.Database.Update(dto);
            }

            if (before != legalHold)
            {
                var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
                auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, dto.EntityType, itemKey)
                {
                    Field = "legalHold",
                    OldValue = before ? "true" : "false",
                    NewValue = (legalHold ? "true" : "false") + (trimmed is null ? string.Empty : "; " + trimmed),
                });
            }

            scope.Complete();
        }

        return retentionService.GetItem(itemKey);
    }

    private static List<DeletionBlock> Blocks(IScope scope, IEnumerable<Guid> keys, DateTime now)
    {
        var keyList = keys.ToList();
        if (keyList.Count == 0)
        {
            return [];
        }

        var names = new Dictionary<Guid, string>();
        foreach (var chunk in keyList.Chunk(InChunk))
        {
            foreach (var node in scope.Database.Fetch<NodeName>(
                scope.SqlContext.Sql($"SELECT uniqueId, text FROM {Constants.DatabaseSchema.Tables.Node} WHERE uniqueId IN (@0)", chunk)))
            {
                names[node.UniqueId] = node.Text ?? string.Empty;
            }
        }

        return FetchRows(scope, keyList)
            .Select(r => ToBlock(r, names.GetValueOrDefault(r.ItemKey, string.Empty), now))
            .OfType<DeletionBlock>()
            .ToList();
    }

    private static List<ItemRetentionDto> FetchRows(IScope scope, IEnumerable<Guid> keys)
    {
        var rows = new List<ItemRetentionDto>();
        foreach (var chunk in keys.Distinct().Chunk(InChunk))
        {
            rows.AddRange(scope.Database.Fetch<ItemRetentionDto>(
                scope.SqlContext.Sql().SelectAll().From<ItemRetentionDto>().WhereIn<ItemRetentionDto>(r => r.ItemKey, chunk)));
        }

        return rows;
    }

    private static DeletionBlock? ToBlock(ItemRetentionDto row, string name, DateTime now)
    {
        var until = row.RetentionUntil is { } u ? RetentionCalculator.AsUtc(u) : (DateTime?)null;
        DeletionBlockReason? reason = row.LegalHold ? DeletionBlockReason.LegalHold
            : row.OpenEnded ? DeletionBlockReason.RetentionOpenEnded
            : until > now ? DeletionBlockReason.Retention
            : null;
        if (reason is null)
        {
            return null;
        }

        return new DeletionBlock(row.ItemKey, row.EntityType, name, reason.Value)
        {
            RetentionUntil = row.OpenEnded ? null : until,
            RetentionClass = GoverningClassName(row),
        };
    }

    private DeletionBlock? FromCalculation(IContentBase item)
    {
        if (retentionService.GetItem(item.Key) is not { } info)
        {
            return null;
        }

        DeletionBlockReason? reason = info.LegalHold ? DeletionBlockReason.LegalHold
            : info.Status == RetentionStatus.Pending && info.Rules.Count > 0 ? DeletionBlockReason.RetentionOpenEnded
            : info.Status == RetentionStatus.Active ? DeletionBlockReason.Retention
            : null;
        return reason is null
            ? null
            : new DeletionBlock(item.Key, info.EntityType, item.Name ?? string.Empty, reason.Value)
            {
                RetentionUntil = info.OpenEnded ? null : info.RetentionUntil,
                RetentionClass = (info.Rules.FirstOrDefault(r => r.Governing) ?? (info.Rules.Count > 0 ? info.Rules[0] : null))?.ClassName,
            };
    }

    private static string? GoverningClassName(ItemRetentionDto row)
    {
        if (string.IsNullOrEmpty(row.Rules))
        {
            return null;
        }

        var rules = JsonSerializer.Deserialize<List<ItemRetentionRuleInfo>>(row.Rules, RetentionService.Json) ?? [];
        return (rules.FirstOrDefault(r => r.ClassKey == row.GoverningClassKey) ?? rules.FirstOrDefault(r => r.Governing) ?? rules.FirstOrDefault())?.ClassName;
    }

    /// <summary>Key and name of an Umbraco node.</summary>
    private sealed class NodeName
    {
        public Guid UniqueId { get; set; }

        public string? Text { get; set; }
    }
}
