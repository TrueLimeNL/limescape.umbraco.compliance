using System.Globalization;
using System.Text.Json;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Lifecycle;

/// <summary>
/// The archive route: an owner or a functional manager confirms that a published material
/// is archived. Under the content write lock, it unpublishes before saving status <c>archived</c> and the archive date, so it leaves the
/// public scope (detail page, Agent API readers, OAI-PMH tombstone) and a retention class with start event
/// <c>archived</c> starts. An archived document is never published or reopened again.
/// </summary>
public interface IArchiveService
{
    /// <summary>Whether the document can be archived, and by this person. Null when it does not exist.</summary>
    ArchiveState? GetState(Guid documentKey, Actor actor, bool isManager);

    /// <summary>
    /// Archives the document for the acting person. <paramref name="isManager"/> says whether the caller holds the
    /// functional manager role of the calling route; otherwise the person must be one of the owners.
    /// </summary>
    Attempt<ArchiveState?, ArchiveOperationStatus> Archive(Guid documentKey, Actor actor, bool isManager, string? reason = null);
}

/// <summary>Archive state of a document.</summary>
/// <param name="DocumentKey">Document key.</param>
/// <param name="Status">Lifecycle status code; null when the document has none.</param>
/// <param name="Published">True while the document is published.</param>
/// <param name="ArchivedUtc">Archive date (UTC); null when not archived.</param>
/// <param name="Supported">True when the document type has a lifecycle that can be archived.</param>
/// <param name="CanArchive">True when this person may archive the document now.</param>
public sealed record ArchiveState(Guid DocumentKey, string? Status, bool Published, DateTime? ArchivedUtc, bool Supported, bool CanArchive)
{
    /// <summary>True when the document is archived.</summary>
    public bool Archived => Status == LifecycleStatuses.Archived || ArchivedUtc is not null;
}

/// <summary>Outcome of <see cref="IArchiveService.Archive"/>.</summary>
public enum ArchiveOperationStatus
{
    Success,
    DocumentNotFound,
    NotSupported,
    ActingUserRequired,
    NotOwner,
    NotPublished,
    AlreadyArchived,
    Cancelled,
}

/// <summary>Default <see cref="IArchiveService"/>.</summary>
public sealed class ArchiveService(
    IContentService contentService,
    ICoreScopeProvider scopeProvider,
    IGatedContentTypes gatedTypes,
    IAuditWriter auditWriter,
    IOptionsMonitor<PublicationGateOptions> gateOptions,
    IOptionsMonitor<RetentionOptions> retentionOptions,
    TimeProvider timeProvider) : IArchiveService
{
    /// <summary>Longest reason kept in the audit log.</summary>
    public const int MaxReasonLength = 500;

    private string StatusAlias => gateOptions.CurrentValue.StatusPropertyAlias;

    private string ArchivedAlias => retentionOptions.CurrentValue.ArchivedDatePropertyAlias;

    /// <inheritdoc />
    public ArchiveState? GetState(Guid documentKey, Actor actor, bool isManager)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return contentService.GetById(documentKey) is { } content ? State(content, actor, isManager) : null;
    }

    /// <inheritdoc />
    public Attempt<ArchiveState?, ArchiveOperationStatus> Archive(Guid documentKey, Actor actor, bool isManager, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // A person confirms an archive, like an approval: a backoffice user or the person in an acting-user token.
        if (!ApprovalService.IsPerson(actor))
        {
            return Fail(ArchiveOperationStatus.ActingUserRequired);
        }

        // The check and the save are one step under the content write lock.
        using var scope = scopeProvider.CreateCoreScope();
        scope.EagerWriteLock(Constants.Locks.ContentTree);

        var content = contentService.GetById(documentKey);
        if (content is null)
        {
            return Fail(ArchiveOperationStatus.DocumentNotFound);
        }

        if (!IsSupported(content))
        {
            return Fail(ArchiveOperationStatus.NotSupported);
        }

        if (!isManager && !IsOwner(content, actor))
        {
            return Fail(ArchiveOperationStatus.NotOwner);
        }

        if (IsArchived(content))
        {
            return Fail(ArchiveOperationStatus.AlreadyArchived);
        }

        if (!content.Published)
        {
            return Fail(ArchiveOperationStatus.NotPublished);
        }

        // Cancelable unpublishing must succeed before any archive fields are saved. Saved notifications can
        // publish side effects before this outer scope completes, so a surrounding scope alone is insufficient.
        using (LifecycleChangeScope.Begin(LifecycleRoutes.Archive))
        {
            if (!contentService.Unpublish(content).Success
                || contentService.GetById(documentKey) is not { } unpublished)
            {
                return Fail(ArchiveOperationStatus.Cancelled);
            }

            var archived = timeProvider.GetUtcNow().UtcDateTime;
            LifecycleStatuses.Write(unpublished, StatusAlias, LifecycleStatuses.Archived);
            unpublished.SetValue(ArchivedAlias, ArchiveValue(unpublished, archived));
            // Unpublish does not raise ContentSaved: save explicitly for field audit and retention calculation.
            if (!contentService.Save(unpublished).Success)
            {
                return Fail(ArchiveOperationStatus.Cancelled);
            }

            content = unpublished;
        }

        auditWriter.Write(new AuditRecord(AuditActions.Archive, AuditEntityTypes.Document, content.Key)
        {
            Field = "reason",
            NewValue = string.IsNullOrWhiteSpace(reason) ? null : AuditValues.Truncate(reason.Trim(), MaxReasonLength),
        });

        scope.Complete();
        return Attempt.SucceedWithStatus<ArchiveState?, ArchiveOperationStatus>(ArchiveOperationStatus.Success, State(content, actor, isManager));
    }

    private ArchiveState State(IContent content, Actor actor, bool isManager)
    {
        var supported = IsSupported(content);
        var archived = supported && IsArchived(content);
        return new ArchiveState(
            content.Key,
            content.HasProperty(StatusAlias) ? LifecycleStatuses.Of(content, StatusAlias) : null,
            content.Published,
            supported ? ArchivedDate(content) : null,
            supported,
            supported && !archived && content.Published && ApprovalService.IsPerson(actor) && (isManager || IsOwner(content, actor)));
    }

    private bool IsSupported(IContent content) =>
        gatedTypes.IsGated(content) && content.HasProperty(StatusAlias)
        && !string.IsNullOrWhiteSpace(ArchivedAlias) && content.HasProperty(ArchivedAlias);

    private bool IsArchived(IContent content) =>
        LifecycleStatuses.Of(content, StatusAlias) == LifecycleStatuses.Archived || ArchivedDate(content) is not null;

    private DateTime? ArchivedDate(IContent content) => RetentionCalculator.ParseDate(content.GetValue(ArchivedAlias));

    /// <summary>Owners are a JSON array of person ids; malformed data never makes anyone an owner.</summary>
    private bool IsOwner(IContent content, Actor actor)
    {
        var raw = content.HasProperty(gateOptions.CurrentValue.OwnersPropertyAlias) ? content.GetValue<string>(gateOptions.CurrentValue.OwnersPropertyAlias) : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            return (JsonSerializer.Deserialize<string[]>(raw) ?? []).Contains(actor.PersonId, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A date property stores the date; a text property the ISO 8601 text, so the retention calculation reads it back.</summary>
    private object ArchiveValue(IContent content, DateTime archivedUtc) =>
        content.Properties[ArchivedAlias]!.PropertyType.ValueStorageType == ValueStorageType.Date
            ? archivedUtc
            : archivedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static Attempt<ArchiveState?, ArchiveOperationStatus> Fail(ArchiveOperationStatus status) =>
        Attempt.FailWithStatus<ArchiveState?, ArchiveOperationStatus>(status, null);
}
