using Limescape.Umbraco.Compliance.ActingUser;
using Microsoft.AspNetCore.Http;

namespace Limescape.Umbraco.Compliance.PublicationGate;

/// <summary>
/// Carries an Umbraco Workflow approval from the moment a reviewer approves to the publish that Workflow performs in the
/// same request. Workflow publishes before it raises any "approved" or "completed" notification, so the gate needs to
/// know about the approval while it is still in progress.
/// </summary>
public interface IWorkflowApprovalTracker
{
    /// <summary>A reviewer is approving the draft with this hash in the current request.</summary>
    void Begin(Guid contentKey, string versionHash, Actor reviewer);

    /// <summary>Takes the pending approval for this exact draft in the current request, if there is one.</summary>
    bool TryTake(Guid contentKey, string versionHash, out Actor? reviewer);

    /// <summary>Forgets a pending approval of the document in the current request (for example after a step that did not complete the flow).</summary>
    void Clear(Guid contentKey);
}

/// <summary>
/// Default <see cref="IWorkflowApprovalTracker"/>: pending approvals live in <see cref="HttpContext.Items"/>, so they
/// never leak to another request and disappear with the request. Outside a request nothing is tracked.
/// </summary>
public sealed class WorkflowApprovalTracker(IHttpContextAccessor httpContextAccessor) : IWorkflowApprovalTracker
{
    private static readonly object ItemsKey = new();

    /// <inheritdoc />
    public void Begin(Guid contentKey, string versionHash, Actor reviewer)
    {
        if (Pending(create: true) is { } pending)
        {
            pending[contentKey] = (versionHash, reviewer);
        }
    }

    /// <inheritdoc />
    public bool TryTake(Guid contentKey, string versionHash, out Actor? reviewer)
    {
        reviewer = null;
        if (Pending(create: false) is not { } pending
            || !pending.TryGetValue(contentKey, out var entry)
            || !string.Equals(entry.VersionHash, versionHash, StringComparison.Ordinal))
        {
            return false;
        }

        pending.Remove(contentKey);
        reviewer = entry.Reviewer;
        return true;
    }

    /// <inheritdoc />
    public void Clear(Guid contentKey) => Pending(create: false)?.Remove(contentKey);

    private Dictionary<Guid, (string VersionHash, Actor Reviewer)>? Pending(bool create)
    {
        var items = httpContextAccessor.HttpContext?.Items;
        if (items is null)
        {
            return null;
        }

        if (items.TryGetValue(ItemsKey, out var value) && value is Dictionary<Guid, (string, Actor)> existing)
        {
            return existing;
        }

        if (!create)
        {
            return null;
        }

        var created = new Dictionary<Guid, (string VersionHash, Actor Reviewer)>();
        items[ItemsKey] = created;
        return created;
    }
}
