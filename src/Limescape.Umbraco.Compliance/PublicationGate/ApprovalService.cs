using System.Text.Json;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Lifecycle;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Records approval decisions and reports the approval state of a document.
/// </summary>
public interface IApprovalService
{
    /// <summary>Approval state of the current draft of the document, or null when the document does not exist.</summary>
    ApprovalStatus? GetStatus(Guid documentKey);

    /// <summary>
    /// Records a decision about the current draft for the acting person. Only a member of a reviewer group may decide,
    /// and the decision must name a person: an API user needs an acting-user token. When the request names a version
    /// hash, it must be the hash of the current draft.
    /// </summary>
    Attempt<ApprovalRecord?, ApprovalOperationStatus> Decide(Guid documentKey, ApprovalDecision decision);

    /// <summary>
    /// Records a decision about the current draft of <paramref name="content"/> for a person whose reviewer role the
    /// calling route has checked itself, for example the Agent API from the person's own token. The same record,
    /// audit entry and status change as <see cref="Decide"/> (source <see cref="ApprovalSources.Integration"/>); the
    /// person, the feedback of a rejection and the owner rule are checked here again. The channel is the route's own
    /// (<c>app</c> or <c>assistant</c>). Callers run this inside their own scope, so a later step (the publish after an
    /// approval) can roll the record back.
    /// </summary>
    Attempt<ApprovalRecord?, ApprovalOperationStatus> DecideAs(IContent content, ApprovalDecision decision, Actor reviewer);

    /// <summary>Whether the owner rule permits this reviewer to approve.</summary>
    bool CanApprove(IContent content, Actor reviewer);

    /// <summary>Records an approval given through Umbraco Workflow for the draft with this hash.</summary>
    ApprovalRecord RecordWorkflowApproval(IContent content, string versionHash, Actor reviewer);

    /// <summary>
    /// Records a rejection given through Umbraco Workflow for the draft with this hash, with the reviewer's comment as
    /// findings, and moves the lifecycle status to rejected. Null when another rule refused the status change (nothing
    /// is recorded then).
    /// </summary>
    ApprovalRecord? RecordWorkflowRejection(IContent content, string versionHash, Actor reviewer, string? comment);
}

/// <summary>What a reviewer decides.</summary>
/// <param name="Decision"><see cref="ApprovalDecisions.Approved"/> or <see cref="ApprovalDecisions.Rejected"/>.</param>
/// <param name="VersionHash">Hash of the version the reviewer looked at; optional, recommended.</param>
/// <param name="Comment">Short comment (at most 2,000 characters).</param>
/// <param name="Findings">Findings of the quality check.</param>
public sealed record ApprovalDecision(string Decision, string? VersionHash = null, string? Comment = null, string? Findings = null);

/// <summary>Approval state of a document's current draft.</summary>
public sealed record ApprovalStatus(
    Guid DocumentKey,
    string DocumentType,
    bool Gated,
    int VersionId,
    string VersionHash,
    ApprovalRecord? CurrentDecision,
    IReadOnlyList<ApprovalRecord> History)
{
    /// <summary>True when the latest decision for the current draft is an approval.</summary>
    public bool Approved => CurrentDecision?.Decision == ApprovalDecisions.Approved;
}

/// <summary>Outcome of <see cref="IApprovalService.Decide"/>.</summary>
public enum ApprovalOperationStatus
{
    Success,
    DocumentNotFound,
    NotReviewer,
    ActingUserRequired,
    ChannelNotAllowed,
    OwnMaterial,
    VersionChanged,
    InvalidDecision,
    RejectionRequiresFeedback,
    Cancelled,
}

/// <summary>
/// Default <see cref="IApprovalService"/>.
/// </summary>
public sealed class ApprovalService(
    IContentService contentService,
    IContentVersionHasher hasher,
    IApprovalStore store,
    IAuditWriter auditWriter,
    IActingUserAccessor actingUserAccessor,
    IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
    IOptionsMonitor<PublicationGateOptions> options,
    IGatedContentTypes gatedTypes,
    IScopeProvider scopeProvider,
    TimeProvider timeProvider) : IApprovalService
{
    /// <inheritdoc />
    public ApprovalStatus? GetStatus(Guid documentKey)
    {
        var content = contentService.GetById(documentKey);
        if (content is null)
        {
            return null;
        }

        var hash = hasher.ComputeHash(content);
        return new ApprovalStatus(
            content.Key,
            content.ContentType.Alias,
            gatedTypes.IsGated(content),
            content.VersionId,
            hash,
            store.GetLatest(content.Key, hash),
            store.GetAll(content.Key));
    }

    /// <inheritdoc />
    public Attempt<ApprovalRecord?, ApprovalOperationStatus> Decide(Guid documentKey, ApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // The reviewer permission belongs to the authenticated Umbraco user. For a trusted integration that is its API
        // user; the integration vouches that the person in the token is a reviewer.
        if (!IsReviewer(backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser))
        {
            return Fail(ApprovalOperationStatus.NotReviewer);
        }

        // A decision names a person: a backoffice user, or the person in an acting-user token. An API user on
        // its own is a technical account and cannot approve.
        var actor = actingUserAccessor.Current;
        if (!IsPerson(actor))
        {
            return Fail(ApprovalOperationStatus.ActingUserRequired);
        }

        if (!IsDecisionChannelAllowed(actor))
        {
            return Fail(ApprovalOperationStatus.ChannelNotAllowed);
        }

        if (!ApprovalDecisions.IsValid(decision.Decision))
        {
            return Fail(ApprovalOperationStatus.InvalidDecision);
        }

        // A rejection goes back to the submitter, who has to know what to change.
        if (decision.Decision == ApprovalDecisions.Rejected && string.IsNullOrWhiteSpace(decision.Comment) && string.IsNullOrWhiteSpace(decision.Findings))
        {
            return Fail(ApprovalOperationStatus.RejectionRequiresFeedback);
        }

        var content = contentService.GetById(documentKey);
        if (content is null)
        {
            return Fail(ApprovalOperationStatus.DocumentNotFound);
        }

        if (decision.Decision == ApprovalDecisions.Approved && !CanApprove(content, actor))
        {
            return Fail(ApprovalOperationStatus.OwnMaterial);
        }

        var hash = hasher.ComputeHash(content);
        if (decision.VersionHash is { Length: > 0 } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ApprovalOperationStatus.VersionChanged);
        }

        var source = actor.FromTrustedIntegration ? ApprovalSources.Integration : ApprovalSources.Backoffice;
        var record = Append(content, hash, decision.Decision, actor, source, decision.Comment, decision.Findings);
        return record is null
            ? Fail(ApprovalOperationStatus.Cancelled)
            : Attempt.SucceedWithStatus<ApprovalRecord?, ApprovalOperationStatus>(ApprovalOperationStatus.Success, record);
    }

    /// <inheritdoc />
    public Attempt<ApprovalRecord?, ApprovalOperationStatus> DecideAs(IContent content, ApprovalDecision decision, Actor reviewer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(reviewer);

        // Only a person named by a validated token decides; the route vouches for the reviewer role.
        if (!reviewer.FromTrustedIntegration)
        {
            return Fail(ApprovalOperationStatus.ActingUserRequired);
        }

        if (!ApprovalDecisions.IsValid(decision.Decision))
        {
            return Fail(ApprovalOperationStatus.InvalidDecision);
        }

        if (decision.Decision == ApprovalDecisions.Rejected && string.IsNullOrWhiteSpace(decision.Comment) && string.IsNullOrWhiteSpace(decision.Findings))
        {
            return Fail(ApprovalOperationStatus.RejectionRequiresFeedback);
        }

        if (decision.Decision == ApprovalDecisions.Approved && !CanApprove(content, reviewer))
        {
            return Fail(ApprovalOperationStatus.OwnMaterial);
        }

        var hash = hasher.ComputeHash(content);
        if (decision.VersionHash is { Length: > 0 } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ApprovalOperationStatus.VersionChanged);
        }

        var record = Append(content, hash, decision.Decision, reviewer, ApprovalSources.Integration, decision.Comment, decision.Findings);
        return record is null
            ? Fail(ApprovalOperationStatus.Cancelled)
            : Attempt.SucceedWithStatus<ApprovalRecord?, ApprovalOperationStatus>(ApprovalOperationStatus.Success, record);
    }

    /// <inheritdoc />
    public ApprovalRecord RecordWorkflowApproval(IContent content, string versionHash, Actor reviewer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reviewer);
        if (!IsPerson(reviewer) || !IsDecisionChannelAllowed(reviewer) || !CanApprove(content, reviewer))
        {
            throw new InvalidOperationException("The workflow reviewer cannot approve this material.");
        }

        return Append(content, versionHash, ApprovalDecisions.Approved, reviewer, ApprovalSources.Workflow, comment: null, findings: null)!;
    }

    /// <inheritdoc />
    public ApprovalRecord? RecordWorkflowRejection(IContent content, string versionHash, Actor reviewer, string? comment)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reviewer);
        if (!IsPerson(reviewer) || !IsDecisionChannelAllowed(reviewer))
        {
            throw new InvalidOperationException("The workflow reviewer cannot reject this material.");
        }

        // Workflow's comment is the feedback for the submitter; it is the findings of this quality check.
        var feedback = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return Append(content, versionHash, ApprovalDecisions.Rejected, reviewer, ApprovalSources.Workflow, comment: null, findings: feedback);
    }

    /// <summary>True when the actor is a person: a backoffice user or the person named in a validated acting-user token.</summary>
    public static bool IsPerson(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.FromTrustedIntegration || actor.Channel == ActorChannels.Backoffice;
    }

    /// <summary>Backoffice people decide directly; acting-user tokens must name app or api.</summary>
    public static bool IsDecisionChannelAllowed(Actor actor) => actor.FromTrustedIntegration
        ? actor.Channel is "app" or "api"
        : actor.Channel == ActorChannels.Backoffice;

    /// <summary>Checks the owner rule; malformed owner data fails closed.</summary>
    public bool CanApprove(IContent content, Actor reviewer)
    {
        if (!options.CurrentValue.RequireDifferentReviewer || !content.HasProperty(options.CurrentValue.OwnersPropertyAlias))
        {
            return true;
        }

        var owners = content.GetValue<string>(options.CurrentValue.OwnersPropertyAlias);
        if (string.IsNullOrWhiteSpace(owners))
        {
            return true;
        }
        try
        {
            return !(JsonSerializer.Deserialize<string[]>(owners) ?? []).Contains(reviewer.PersonId, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Attempt<ApprovalRecord?, ApprovalOperationStatus> Fail(ApprovalOperationStatus status) =>
        Attempt.FailWithStatus<ApprovalRecord?, ApprovalOperationStatus>(status, null);

    private bool IsReviewer(IUser? user) =>
        user is not null && options.CurrentValue.IsReviewerGroup(user.Groups.Select(g => g.Alias));

    /// <summary>
    /// Stores the decision and its audit entry; a rejection also moves the lifecycle status to rejected in the same
    /// scope. Null, with nothing stored, when another rule cancels that save.
    /// </summary>
    private ApprovalRecord? Append(IContent content, string hash, string decision, Actor actor, string source, string? comment, string? findings)
    {
        using var scope = scopeProvider.CreateScope();
        var record = store.Add(new ApprovalRecord(content.Key, content.VersionId, hash, decision)
        {
            ReviewerId = actor.PersonId,
            ReviewerName = actor.Name,
            Channel = actor.Channel,
            Source = source,
            Comment = comment,
            Findings = findings,
            DecidedUtc = timeProvider.GetUtcNow().UtcDateTime,
            TechnicalUserKey = actor.TechnicalUserKey,
            CorrelationId = actor.CorrelationId,
        });

        // The audit log records the decision with the same acting person; both commit or roll back together.
        auditWriter.Write(new AuditRecord(AuditActions.Approval, AuditEntityTypes.Document, content.Key)
        {
            Field = "decision",
            NewValue = $"{decision} version {content.VersionId} {hash} ({source})",
        });

        if (decision == ApprovalDecisions.Rejected && !MarkRejected(content))
        {
            return null;
        }

        scope.Complete();
        return record;
    }

    /// <summary>
    /// A rejected draft goes back to its owner: status rejected, so the Agent API and the backoffice let the owner change
    /// and resubmit it. A published document keeps its status (the rejection concerns a new draft of it); an archived one
    /// is never reopened. False when another rule cancelled the save.
    /// </summary>
    private bool MarkRejected(IContent content)
    {
        var alias = options.CurrentValue.StatusPropertyAlias;
        var stored = contentService.GetById(content.Key);
        if (stored is null || stored.Published || !stored.HasProperty(alias)
            || LifecycleStatuses.Of(stored, alias) is LifecycleStatuses.Rejected or LifecycleStatuses.Archived)
        {
            return true;
        }

        LifecycleStatuses.Write(stored, alias, LifecycleStatuses.Rejected);
        using var route = LifecycleChangeScope.Begin(LifecycleRoutes.Rejection);
        return contentService.Save(stored, backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser?.Id ?? -1).Success;
    }
}
