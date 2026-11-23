using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Retention classes, assignments and the computed retention per item.
/// Every change to classes, assignments or an item's effective period is written to the audit log.
/// </summary>
public interface IRetentionService
{
    /// <summary>All classes, by name.</summary>
    IReadOnlyList<RetentionClass> GetClasses();

    /// <summary>One class, or null.</summary>
    RetentionClass? GetClass(Guid key);

    /// <summary>Creates a class. Throws <see cref="RetentionValidationException"/> on invalid input.</summary>
    RetentionClass CreateClass(RetentionClassInput input);

    /// <summary>
    /// Updates a class and recalculates every item it may apply to. A shorter period is only stored for items when
    /// <paramref name="confirmShortening"/> is true (an administrator confirmed it); otherwise the longer period is kept.
    /// Returns null when the class does not exist.
    /// </summary>
    (RetentionClass Class, RetentionRecalculation Recalculation)? UpdateClass(Guid key, RetentionClassInput input, bool confirmShortening);

    /// <summary>Deletes a class without assignments. Returns false when it does not exist; throws when it is still assigned.</summary>
    bool DeleteClass(Guid key);

    /// <summary>All assignments.</summary>
    IReadOnlyList<RetentionAssignment> GetAssignments();

    /// <summary>Creates an assignment and calculates the items it applies to. Throws <see cref="RetentionValidationException"/> on invalid input.</summary>
    (RetentionAssignment Assignment, RetentionRecalculation Recalculation) CreateAssignment(RetentionAssignment input);

    /// <summary>Deletes an assignment and recalculates the items it applied to; see <see cref="UpdateClass"/> for shortening. Null when it does not exist.</summary>
    RetentionRecalculation? DeleteAssignment(int id, bool confirmShortening);

    /// <summary>Retention of an item: the stored period, or a fresh calculation when none is stored. Null when the item does not exist.</summary>
    ItemRetentionInfo? GetItem(Guid itemKey);

    /// <summary>Recalculates one item by key. Null when the item does not exist.</summary>
    RetentionRecalculation? RecalculateItem(Guid itemKey, bool confirmShortening);

    /// <summary>Recalculates one loaded item (used by the notification handlers). Never shortens.</summary>
    RetentionRecalculation Recalculate(IContentBase item);

    /// <summary>Removes the stored retention of an item that was deleted permanently.</summary>
    void Forget(Guid itemKey);

    /// <summary>Items whose period ended at or before <paramref name="asOfUtc"/>, oldest end first.</summary>
    IReadOnlyList<ItemRetentionInfo> GetEnded(DateTime asOfUtc, int skip, int take, bool onlyUnreported = false);

    /// <summary>Items whose period has started and not ended at <paramref name="asOfUtc"/>, the earliest end first.</summary>
    IReadOnlyList<ItemRetentionInfo> GetRunning(DateTime asOfUtc, int skip, int take);

    /// <summary>Marks items as reported by the retention job.</summary>
    void MarkEndReported(IEnumerable<Guid> itemKeys, DateTime reportedUtc);
}

/// <summary>
/// Default <see cref="IRetentionService"/> on the package tables.
/// </summary>
public sealed partial class RetentionService(
    IScopeProvider scopeProvider,
    IContentService contentService,
    IMediaService mediaService,
    IContentTypeService contentTypeService,
    IMediaTypeService mediaTypeService,
    IAuditWriter auditWriter,
    IOptionsMonitor<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger) : IRetentionService
{
    private const int PageSize = 500;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public IReadOnlyList<RetentionClass> GetClasses()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return LoadClasses(scope).Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public RetentionClass? GetClass(Guid key)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return FindClassDto(scope, key) is { } dto ? ToModel(dto) : null;
    }

    /// <inheritdoc />
    public RetentionClass CreateClass(RetentionClassInput input)
    {
        Validate(input);
        var now = UtcNow;
        var dto = new RetentionClassDto { Key = Guid.NewGuid(), CreatedUtc = now };
        Apply(dto, input, now);

        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(dto);
        var created = ToModel(dto);
        auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, RetentionAuditEntityTypes.RetentionClass, created.Key)
        {
            Field = "class",
            NewValue = Describe(created),
        });
        scope.Complete();
        return created;
    }

    /// <inheritdoc />
    public (RetentionClass Class, RetentionRecalculation Recalculation)? UpdateClass(Guid key, RetentionClassInput input, bool confirmShortening)
    {
        Validate(input);
        RetentionClass before;
        RetentionClass after;
        using (var scope = scopeProvider.CreateScope())
        {
            if (FindClassDto(scope, key) is not { } dto)
            {
                return null;
            }

            before = ToModel(dto);
            Apply(dto, input, UtcNow);
            scope.Database.Update(dto);
            after = ToModel(dto);
            auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, RetentionAuditEntityTypes.RetentionClass, key)
            {
                Field = "class",
                OldValue = Describe(before),
                NewValue = Describe(after),
            });
            scope.Complete();
        }

        var assignments = GetAssignments().Where(a => a.ClassKey == key).ToList();
        var recalculation = RecalculateMany(AffectedItems(assignments, key), confirmShortening);
        return (after, recalculation);
    }

    /// <inheritdoc />
    public bool DeleteClass(Guid key)
    {
        using var scope = scopeProvider.CreateScope();
        if (FindClassDto(scope, key) is not { } dto)
        {
            return false;
        }

        var inUse = scope.Database.ExecuteScalar<int>(
            scope.SqlContext.Sql().SelectCount().From<RetentionAssignmentDto>().Where<RetentionAssignmentDto>(a => a.RetentionClassId == dto.Id));
        if (inUse > 0)
        {
            throw new RetentionValidationException(RetentionErrorCodes.ClassInUse, $"The retention class is still assigned {inUse} time(s); remove the assignments first.");
        }

        scope.Database.Delete(dto);
        auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, RetentionAuditEntityTypes.RetentionClass, key)
        {
            Field = "class",
            OldValue = Describe(ToModel(dto)),
        });
        scope.Complete();
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<RetentionAssignment> GetAssignments()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return LoadAssignments(scope);
    }

    /// <inheritdoc />
    public (RetentionAssignment Assignment, RetentionRecalculation Recalculation) CreateAssignment(RetentionAssignment input)
    {
        ArgumentNullException.ThrowIfNull(input);

        RetentionAssignment created;
        using (var scope = scopeProvider.CreateScope())
        {
            if (FindClassDto(scope, input.ClassKey) is not { } classDto)
            {
                throw new RetentionValidationException(RetentionErrorCodes.ClassNotFound, "The retention class does not exist.");
            }

            var dto = ValidateAndMap(input, classDto.Id);
            scope.Database.Insert(dto);
            created = ToModel(dto, classDto.Key);
            auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, RetentionAuditEntityTypes.RetentionAssignment, created.ItemKey ?? created.ContentTypeKey)
            {
                Field = "assignment",
                NewValue = Describe(created, classDto.Name),
            });
            scope.Complete();
        }

        return (created, RecalculateMany(AffectedItems([created], classKey: null), confirmShortening: false));
    }

    /// <inheritdoc />
    public RetentionRecalculation? DeleteAssignment(int id, bool confirmShortening)
    {
        RetentionAssignment removed;
        using (var scope = scopeProvider.CreateScope())
        {
            var dto = scope.Database.SingleOrDefault<RetentionAssignmentDto>(
                scope.SqlContext.Sql().SelectAll().From<RetentionAssignmentDto>().Where<RetentionAssignmentDto>(a => a.Id == id));
            if (dto is null)
            {
                return null;
            }

            var classDto = scope.Database.SingleOrDefault<RetentionClassDto>(
                scope.SqlContext.Sql().SelectAll().From<RetentionClassDto>().Where<RetentionClassDto>(c => c.Id == dto.RetentionClassId));
            removed = ToModel(dto, classDto?.Key ?? Guid.Empty);
            scope.Database.Delete(dto);
            auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, RetentionAuditEntityTypes.RetentionAssignment, removed.ItemKey ?? removed.ContentTypeKey)
            {
                Field = "assignment",
                OldValue = Describe(removed, classDto?.Name),
            });
            scope.Complete();
        }

        return RecalculateMany(AffectedItems([removed], removed.ClassKey), confirmShortening);
    }

    /// <inheritdoc />
    public ItemRetentionInfo? GetItem(Guid itemKey)
    {
        ItemRetentionDto? stored;
        using (var scope = scopeProvider.CreateScope(autoComplete: true))
        {
            stored = FindItemDto(scope, itemKey);
        }

        if (stored is not null)
        {
            return ToInfo(stored);
        }

        if (Load(itemKey) is not { } item)
        {
            return null;
        }

        // Nothing stored yet (for example an item saved before the package was installed): calculate for display only.
        var (result, classes) = Calculate(item);
        return new ItemRetentionInfo(item.Key, EntityType(item), result.StatusAt(UtcNow), ToRuleInfos(result))
        {
            RetentionUntil = result.Until,
            StartDate = result.Governing?.StartDate,
            OpenEnded = result.IsOpenEnded,
            GoverningClassKey = result.Governing?.Class.Key,
            EndAction = result.Governing?.Class.EndAction,
        };
    }

    /// <inheritdoc />
    public RetentionRecalculation? RecalculateItem(Guid itemKey, bool confirmShortening) =>
        Load(itemKey) is { } item ? Recalculate(item, confirmShortening) : null;

    /// <inheritdoc />
    public RetentionRecalculation Recalculate(IContentBase item) => Recalculate(item, confirmShortening: false);

    /// <inheritdoc />
    public void Forget(Guid itemKey)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Execute(scope.SqlContext.Sql().Delete<ItemRetentionDto>().Where<ItemRetentionDto>(r => r.ItemKey == itemKey));
        scope.Complete();
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemRetentionInfo> GetEnded(DateTime asOfUtc, int skip, int take, bool onlyUnreported = false)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .SelectAll()
            .From<ItemRetentionDto>()
            .Where<ItemRetentionDto>(r => !r.OpenEnded && r.RetentionUntil != null && r.RetentionUntil <= asOfUtc);
        if (onlyUnreported)
        {
            sql = sql.Where<ItemRetentionDto>(r => r.EndReportedUtc == null);
        }

        sql = sql.OrderBy<ItemRetentionDto>(r => r.RetentionUntil).OrderBy<ItemRetentionDto>(r => r.Id);
        return scope.Database.SkipTake<ItemRetentionDto>(skip, take, sql).Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemRetentionInfo> GetRunning(DateTime asOfUtc, int skip, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var sql = scope.SqlContext.Sql()
            .SelectAll()
            .From<ItemRetentionDto>()
            .Where<ItemRetentionDto>(r => !r.OpenEnded && r.StartDate != null && r.RetentionUntil != null && r.RetentionUntil > asOfUtc)
            .OrderBy<ItemRetentionDto>(r => r.RetentionUntil)
            .OrderBy<ItemRetentionDto>(r => r.Id);
        return scope.Database.SkipTake<ItemRetentionDto>(skip, take, sql).Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public void MarkEndReported(IEnumerable<Guid> itemKeys, DateTime reportedUtc)
    {
        ArgumentNullException.ThrowIfNull(itemKeys);
        using var scope = scopeProvider.CreateScope();
        foreach (var key in itemKeys)
        {
            scope.Database.Execute(
                scope.SqlContext.Sql().Update<ItemRetentionDto>(u => u.Set(r => r.EndReportedUtc, reportedUtc)).Where<ItemRetentionDto>(r => r.ItemKey == key));
        }

        scope.Complete();
    }

    private RetentionRecalculation Recalculate(IContentBase item, bool confirmShortening)
    {
        var (result, _) = Calculate(item);
        var now = UtcNow;

        using var scope = scopeProvider.CreateScope();
        var stored = FindItemDto(scope, item.Key);
        if (stored is null && result.Rules.Count == 0)
        {
            scope.Complete();
            return RetentionRecalculation.Empty;
        }

        var merged = RetentionCalculator.Merge(stored?.OpenEnded ?? false, stored?.RetentionUntil is { } u ? RetentionCalculator.AsUtc(u) : null, result, confirmShortening);
        var changed = stored is null
            ? merged.OpenEnded || merged.Until is not null
            : stored.OpenEnded != merged.OpenEnded || !Equals(stored.RetentionUntil is { } su ? RetentionCalculator.AsUtc(su) : (DateTime?)null, merged.Until);

        var dto = stored ?? new ItemRetentionDto { ItemKey = item.Key, EntityType = EntityType(item) };
        var oldValue = stored is null ? null : DescribePeriod(stored.OpenEnded, stored.RetentionUntil);
        dto.OpenEnded = merged.OpenEnded;
        dto.RetentionUntil = merged.Until;
        dto.HeldFromEarlier = merged.ShorteningRefused;
        if (!merged.ShorteningRefused)
        {
            dto.GoverningClassKey = result.Governing?.Class.Key;
            dto.StartDate = result.Governing?.StartDate;
        }

        dto.Rules = JsonSerializer.Serialize(ToRuleInfos(result), Json);
        dto.CalculatedUtc = now;
        if (changed)
        {
            // A new or extended period is reported again when it ends.
            dto.EndReportedUtc = null;
        }

        if (stored is null)
        {
            scope.Database.Insert(dto);
        }
        else
        {
            scope.Database.Update(dto);
        }

        if (changed)
        {
            auditWriter.Write(new AuditRecord(AuditActions.RetentionChange, dto.EntityType, item.Key)
            {
                Field = "retentionUntil",
                OldValue = oldValue,
                NewValue = DescribePeriod(dto.OpenEnded, dto.RetentionUntil),
            });
        }

        scope.Complete();

        if (merged.ShorteningRefused)
        {
            LogShorteningRefused(logger, item.Key, dto.RetentionUntil, dto.OpenEnded);
        }

        return new RetentionRecalculation(1, changed ? 1 : 0, merged.ShorteningRefused ? [item.Key] : []);
    }

    private (RetentionResult Result, IReadOnlyDictionary<Guid, RetentionClass> Classes) Calculate(IContentBase item)
    {
        Dictionary<Guid, RetentionClass> classes;
        List<RetentionAssignment> assignments;
        using (var scope = scopeProvider.CreateScope(autoComplete: true))
        {
            assignments = LoadAssignments(scope);
            classes = assignments.Count == 0 ? [] : LoadClasses(scope);
        }

        if (assignments.Count == 0)
        {
            return (RetentionResult.None, classes);
        }

        var subject = Subject(item);
        return (RetentionCalculator.Calculate(subject, RetentionCalculator.Applicable(subject, assignments, classes)), classes);
    }

    private RetentionSubject Subject(IContentBase item)
    {
        IEnumerable<object?> Values(string alias) =>
            item.Properties.TryGetValue(alias, out var property) && property is not null
                ? property.Values.Select(v => v.EditedValue ?? v.PublishedValue)
                : [];

        var archiveAlias = options.CurrentValue.ArchivedDatePropertyAlias;
        return new RetentionSubject(item.Key, item.ContentType.Key, RetentionCalculator.AsUtc(item.UpdateDate))
        {
            Published = item switch
            {
                IContent content => content.PublishDate is { } p ? RetentionCalculator.AsUtc(p) : null,
                _ => RetentionCalculator.AsUtc(item.CreateDate),
            },
            Archived = string.IsNullOrWhiteSpace(archiveAlias) ? null : Values(archiveAlias).Select(RetentionCalculator.ParseDate).FirstOrDefault(d => d is not null),
            PropertyValues = Values,
        };
    }

    private RetentionRecalculation RecalculateMany(IEnumerable<IContentBase> items, bool confirmShortening)
    {
        var total = RetentionRecalculation.Empty;
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (seen.Add(item.Key))
            {
                total = total.Add(Recalculate(item, confirmShortening));
            }
        }

        return total;
    }

    /// <summary>Items an assignment may apply to, plus items whose stored rules mention the class.</summary>
    private IEnumerable<IContentBase> AffectedItems(IReadOnlyList<RetentionAssignment> assignments, Guid? classKey)
    {
        foreach (var assignment in assignments)
        {
            switch (assignment.Target)
            {
                case RetentionAssignmentTarget.Item when assignment.ItemKey is { } itemKey:
                    if (Load(itemKey) is { } single)
                    {
                        yield return single;
                    }

                    break;
                case RetentionAssignmentTarget.ContentType when assignment.ContentTypeKey is { } typeKey:
                    foreach (var item in ItemsOfType(typeKey))
                    {
                        yield return item;
                    }

                    break;
                case RetentionAssignmentTarget.PropertyValue:
                    var typeKeys = assignment.ContentTypeKey is { } only
                        ? [only]
                        : TypesWithProperty(assignment.PropertyAlias!);
                    foreach (var typeKey in typeKeys)
                    {
                        foreach (var item in ItemsOfType(typeKey))
                        {
                            yield return item;
                        }
                    }

                    break;
            }
        }

        if (classKey is { } key)
        {
            List<Guid> storedKeys;
            using (var scope = scopeProvider.CreateScope(autoComplete: true))
            {
                var pattern = "%" + key.ToString() + "%";
                storedKeys = scope.Database.Fetch<Guid>(
                    scope.SqlContext.Sql().Select("itemKey").From<ItemRetentionDto>()
                        .Where("rules LIKE @0", pattern));
            }

            foreach (var storedKey in storedKeys)
            {
                if (Load(storedKey) is { } storedItem)
                {
                    yield return storedItem;
                }
            }
        }
    }

    private IEnumerable<IContentBase> ItemsOfType(Guid typeKey)
    {
        if (contentTypeService.Get(typeKey) is { } documentType)
        {
            for (long page = 0; ; page++)
            {
                var items = contentService.GetPagedOfType(documentType.Id, page, PageSize, out var total, null!, null).ToList();
                foreach (var item in items)
                {
                    yield return item;
                }

                if ((page + 1) * PageSize >= total || items.Count == 0)
                {
                    break;
                }
            }
        }
        else if (mediaTypeService.Get(typeKey) is { } mediaType)
        {
            for (long page = 0; ; page++)
            {
                var items = mediaService.GetPagedOfType(mediaType.Id, page, PageSize, out var total, null!, null).ToList();
                foreach (var item in items)
                {
                    yield return item;
                }

                if ((page + 1) * PageSize >= total || items.Count == 0)
                {
                    break;
                }
            }
        }
    }

    private List<Guid> TypesWithProperty(string alias) =>
        contentTypeService.GetAll().Cast<IContentTypeComposition>()
            .Concat(mediaTypeService.GetAll())
            .Where(t => t.CompositionPropertyTypes.Any(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Key)
            .ToList();

    private IContentBase? Load(Guid key) => (IContentBase?)contentService.GetById(key) ?? mediaService.GetById(key);

    private RetentionAssignmentDto ValidateAndMap(RetentionAssignment input, int classId)
    {
        var dto = new RetentionAssignmentDto
        {
            RetentionClassId = classId,
            TargetType = Storage(input.Target),
            CreatedUtc = UtcNow,
        };

        switch (input.Target)
        {
            case RetentionAssignmentTarget.ContentType:
                dto.ContentTypeKey = input.ContentTypeKey is { } typeKey && TypeExists(typeKey)
                    ? typeKey
                    : throw new RetentionValidationException(RetentionErrorCodes.ContentTypeNotFound, "contentTypeKey must be an existing document or media type.");
                break;
            case RetentionAssignmentTarget.PropertyValue:
                if (input.ContentTypeKey is { } onlyType && !TypeExists(onlyType))
                {
                    throw new RetentionValidationException(RetentionErrorCodes.ContentTypeNotFound, "contentTypeKey must be an existing document or media type.");
                }

                dto.ContentTypeKey = input.ContentTypeKey;
                dto.PropertyAlias = Required(input.PropertyAlias, "propertyAlias", 255);
                dto.PropertyValue = Required(input.PropertyValue, "propertyValue", 255);
                break;
            case RetentionAssignmentTarget.Item:
                dto.ItemKey = input.ItemKey is { } itemKey && Load(itemKey) is not null
                    ? itemKey
                    : throw new RetentionValidationException(RetentionErrorCodes.ItemNotFound, "itemKey must be an existing document or media item.");
                break;
            default:
                throw new RetentionValidationException(RetentionErrorCodes.Invalid, "Unknown assignment target.");
        }

        return dto;
    }

    private bool TypeExists(Guid key) => contentTypeService.Get(key) is not null || mediaTypeService.Get(key) is not null;

    private static void Validate(RetentionClassInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Required(input.Name, "name", 255);
        if (input.Description is { Length: > 2000 })
        {
            throw new RetentionValidationException(RetentionErrorCodes.Invalid, "description is longer than 2000 characters.");
        }

        var d = input.Duration;
        if (d.Years < 0 || d.Months < 0 || d.Days < 0 || d.IsZero || d.Years > 200 || d.Months > 2400 || d.Days > 73000)
        {
            throw new RetentionValidationException(RetentionErrorCodes.Invalid, "duration must be positive (years, months and days zero or more, not all zero) and at most 200 years.");
        }

        if (!Enum.IsDefined(input.StartEvent) || !Enum.IsDefined(input.EndAction))
        {
            throw new RetentionValidationException(RetentionErrorCodes.Invalid, "Unknown startEvent or endAction.");
        }

        if (input.StartEvent == RetentionStartEvent.DateProperty)
        {
            Required(input.StartPropertyAlias, "startPropertyAlias", 255);
        }
    }

    private static string Required(string? value, string name, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new RetentionValidationException(RetentionErrorCodes.Invalid, $"{name} is required.")
            : value.Trim().Length > maxLength
                ? throw new RetentionValidationException(RetentionErrorCodes.Invalid, $"{name} is longer than {maxLength} characters.")
                : value.Trim();

    private static void Apply(RetentionClassDto dto, RetentionClassInput input, DateTime now)
    {
        dto.Name = input.Name.Trim();
        dto.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        dto.DurationYears = input.Duration.Years;
        dto.DurationMonths = input.Duration.Months;
        dto.DurationDays = input.Duration.Days;
        dto.StartEvent = Storage(input.StartEvent);
        dto.StartPropertyAlias = input.StartEvent == RetentionStartEvent.DateProperty ? input.StartPropertyAlias!.Trim() : null;
        dto.EndAction = Storage(input.EndAction);
        dto.UpdatedUtc = now;
    }

    private ItemRetentionInfo ToInfo(ItemRetentionDto dto)
    {
        var until = dto.RetentionUntil is { } u ? RetentionCalculator.AsUtc(u) : (DateTime?)null;
        var rules = string.IsNullOrEmpty(dto.Rules)
            ? []
            : JsonSerializer.Deserialize<List<ItemRetentionRuleInfo>>(dto.Rules, Json) ?? [];
        return new ItemRetentionInfo(dto.ItemKey, dto.EntityType, RetentionCalculator.Status(rules.Count > 0 || dto.GoverningClassKey is not null, dto.OpenEnded, until, UtcNow), rules)
        {
            RetentionUntil = until,
            StartDate = dto.StartDate is { } start ? RetentionCalculator.AsUtc(start) : null,
            OpenEnded = dto.OpenEnded,
            GoverningClassKey = dto.GoverningClassKey,
            EndAction = rules.FirstOrDefault(r => r.ClassKey == dto.GoverningClassKey)?.EndAction,
            HeldFromEarlier = dto.HeldFromEarlier,
            LegalHold = dto.LegalHold,
            CalculatedUtc = RetentionCalculator.AsUtc(dto.CalculatedUtc),
            EndReportedUtc = dto.EndReportedUtc is { } r ? RetentionCalculator.AsUtc(r) : null,
        };
    }

    private List<ItemRetentionRuleInfo> ToRuleInfos(RetentionResult result)
    {
        var archiveAlias = options.CurrentValue.ArchivedDatePropertyAlias;
        return result.Rules.Select((r, i) => new ItemRetentionRuleInfo(r.Class.Key, r.Class.Name, r.Class.StartEvent, r.Class.Duration, r.Class.EndAction, r.Source)
        {
            StartPropertyAlias = r.Class.StartEvent switch
            {
                RetentionStartEvent.DateProperty => r.Class.StartPropertyAlias,
                RetentionStartEvent.Archived => archiveAlias,
                _ => null,
            },
            StartDate = r.StartDate,
            Until = r.Until,
            Governing = i == 0,
        }).ToList();
    }

    private static Dictionary<Guid, RetentionClass> LoadClasses(IScope scope) =>
        scope.Database.Fetch<RetentionClassDto>(scope.SqlContext.Sql().SelectAll().From<RetentionClassDto>())
            .Select(ToModel)
            .ToDictionary(c => c.Key);

    private static List<RetentionAssignment> LoadAssignments(IScope scope)
    {
        var classKeys = scope.Database.Fetch<RetentionClassDto>(scope.SqlContext.Sql().SelectAll().From<RetentionClassDto>())
            .ToDictionary(c => c.Id, c => c.Key);
        return scope.Database.Fetch<RetentionAssignmentDto>(scope.SqlContext.Sql().SelectAll().From<RetentionAssignmentDto>().OrderBy<RetentionAssignmentDto>(a => a.Id))
            .Where(a => classKeys.ContainsKey(a.RetentionClassId))
            .Select(a => ToModel(a, classKeys[a.RetentionClassId]))
            .ToList();
    }

    private static RetentionClassDto? FindClassDto(IScope scope, Guid key) =>
        scope.Database.SingleOrDefault<RetentionClassDto>(scope.SqlContext.Sql().SelectAll().From<RetentionClassDto>().Where<RetentionClassDto>(c => c.Key == key));

    private static ItemRetentionDto? FindItemDto(IScope scope, Guid itemKey) =>
        scope.Database.SingleOrDefault<ItemRetentionDto>(scope.SqlContext.Sql().SelectAll().From<ItemRetentionDto>().Where<ItemRetentionDto>(r => r.ItemKey == itemKey));

    private static RetentionClass ToModel(RetentionClassDto dto) =>
        new(dto.Key, dto.Name, new RetentionDuration(dto.DurationYears, dto.DurationMonths, dto.DurationDays), Parse<RetentionStartEvent>(dto.StartEvent), Parse<RetentionEndAction>(dto.EndAction))
        {
            Description = dto.Description,
            StartPropertyAlias = dto.StartPropertyAlias,
            CreatedUtc = RetentionCalculator.AsUtc(dto.CreatedUtc),
            UpdatedUtc = RetentionCalculator.AsUtc(dto.UpdatedUtc),
        };

    private static RetentionAssignment ToModel(RetentionAssignmentDto dto, Guid classKey) =>
        new(dto.Id, classKey, Parse<RetentionAssignmentTarget>(dto.TargetType))
        {
            ContentTypeKey = dto.ContentTypeKey,
            PropertyAlias = dto.PropertyAlias,
            PropertyValue = dto.PropertyValue,
            ItemKey = dto.ItemKey,
            CreatedUtc = RetentionCalculator.AsUtc(dto.CreatedUtc),
        };

    /// <summary>Enum values are stored camelCase (archived, lastModified, allowDeletion, contentType, …).</summary>
    internal static string Storage<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static TEnum Parse<TEnum>(string value)
        where TEnum : struct, Enum => Enum.Parse<TEnum>(value, ignoreCase: true);

    private static string EntityType(IContentBase item) => item is IMedia ? AuditEntityTypes.Media : AuditEntityTypes.Document;

    private static string DescribePeriod(bool openEnded, DateTime? until) =>
        openEnded ? "open-ended" : until is { } u ? RetentionCalculator.AsUtc(u).ToString("O", CultureInfo.InvariantCulture) : "none";

    private static string Describe(RetentionClass c) =>
        $"{c.Name}; {c.Duration}; start {Storage(c.StartEvent)}{(c.StartPropertyAlias is null ? string.Empty : " (" + c.StartPropertyAlias + ")")}; end {Storage(c.EndAction)}";

    private static string Describe(RetentionAssignment a, string? className) => a.Target switch
    {
        RetentionAssignmentTarget.ContentType => $"{className} -> contentType {a.ContentTypeKey}",
        RetentionAssignmentTarget.PropertyValue => $"{className} -> {a.PropertyAlias} = {a.PropertyValue}{(a.ContentTypeKey is null ? string.Empty : " on " + a.ContentTypeKey)}",
        _ => $"{className} -> item {a.ItemKey}",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention of {ItemKey} kept at {Until} (open-ended: {OpenEnded}): the new calculation is shorter and was not confirmed by an administrator")]
    private static partial void LogShorteningRefused(ILogger logger, Guid itemKey, DateTime? until, bool openEnded);
}

/// <summary>
/// Entity types the retention engine writes to the audit log, next to <see cref="AuditEntityTypes"/>.
/// </summary>
public static class RetentionAuditEntityTypes
{
    public const string RetentionClass = "retentionClass";
    public const string RetentionAssignment = "retentionAssignment";
}
