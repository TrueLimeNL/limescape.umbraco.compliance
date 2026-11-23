using Umbraco.Cms.Core.Notifications;

namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Raised by <see cref="RetentionJob"/> for items whose retention period ended since the previous run.
/// Handle it to notify a functional manager, open a review task or start a disposal workflow. The package itself
/// never deletes anything; with end action <see cref="RetentionEndAction.AllowDeletion"/> deletion simply becomes possible.
/// </summary>
/// <param name="Items">Items whose period ended, with the end action of the governing class.</param>
public sealed class RetentionEndedNotification(IReadOnlyList<ItemRetentionInfo> Items) : INotification
{
    /// <summary>Items whose period ended.</summary>
    public IReadOnlyList<ItemRetentionInfo> Items { get; } = Items;

    /// <summary>Items whose governing class asks for review.</summary>
    public IEnumerable<ItemRetentionInfo> ToReview => Items.Where(i => i.EndAction == RetentionEndAction.Notify);
}
