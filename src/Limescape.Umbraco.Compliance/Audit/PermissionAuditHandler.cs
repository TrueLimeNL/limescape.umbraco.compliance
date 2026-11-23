using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Audit of permission changes (action <see cref="AuditActions.PermissionChange"/>): a user group's
/// sections, permissions, granular (node) permissions and start nodes, a deleted group, users added to or removed from a
/// group, and a user's group membership. One entry per changed field, like content updates.
/// </summary>
public sealed partial class PermissionAuditHandler(
    IAuditWriter auditWriter,
    IUserGroupService userGroupService,
    IUserService userService,
    ILogger<PermissionAuditHandler> logger)
    : INotificationAsyncHandler<UserGroupSavingNotification>,
      INotificationAsyncHandler<UserGroupSavedNotification>,
      INotificationAsyncHandler<UserGroupDeletedNotification>,
      INotificationAsyncHandler<UserGroupWithUsersSavedNotification>,
      INotificationAsyncHandler<AssignedUserGroupPermissionsNotification>,
      INotificationHandler<UserSavingNotification>,
      INotificationHandler<UserSavedNotification>
{
    /// <summary>Entity type of user group entries.</summary>
    public const string UserGroupEntityType = "userGroup";

    /// <summary>Entity type of user entries.</summary>
    public const string UserEntityType = "user";

    private const string BeforeStateKey = CompliancePackage.Id + ".Audit.PermissionsBefore";

    /// <inheritdoc />
    public async Task HandleAsync(UserGroupSavingNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            var before = new Dictionary<Guid, ContentSnapshot?>();
            foreach (var group in notification.SavedEntities)
            {
                before[group.Key] = group.HasIdentity && await userGroupService.GetAsync(group.Key).ConfigureAwait(false) is { } stored ? Snapshot(stored) : null;
            }

            notification.State[BeforeStateKey] = before;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public Task HandleAsync(UserGroupSavedNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Safely(() =>
        {
            if (!notification.State.TryGetValue(BeforeStateKey, out var state) || state is not Dictionary<Guid, ContentSnapshot?> before)
            {
                return;
            }

            foreach (var group in notification.SavedEntities)
            {
                if (before.TryGetValue(group.Key, out var snapshot))
                {
                    WriteChanges(UserGroupEntityType, group.Key, snapshot ?? ContentSnapshot.Empty, Snapshot(group));
                }
            }
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(UserGroupDeletedNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Safely(() =>
        {
            foreach (var group in notification.DeletedEntities)
            {
                auditWriter.Write(new AuditRecord(AuditActions.PermissionChange, UserGroupEntityType, group.Key) { Field = "@deleted", OldValue = group.Alias });
            }
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(UserGroupWithUsersSavedNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Safely(() =>
        {
            foreach (var entry in notification.SavedEntities)
            {
                foreach (var user in entry.AddedUsers)
                {
                    auditWriter.Write(new AuditRecord(AuditActions.PermissionChange, UserEntityType, user.Key) { Field = "groups", NewValue = "+" + entry.UserGroup.Alias });
                }

                foreach (var user in entry.RemovedUsers)
                {
                    auditWriter.Write(new AuditRecord(AuditActions.PermissionChange, UserEntityType, user.Key) { Field = "groups", OldValue = entry.UserGroup.Alias, NewValue = "-" + entry.UserGroup.Alias });
                }
            }
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task HandleAsync(AssignedUserGroupPermissionsNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            foreach (var permission in notification.EntityPermissions)
            {
                var group = await userGroupService.GetAsync(permission.UserGroupId).ConfigureAwait(false);
                auditWriter.Write(new AuditRecord(AuditActions.PermissionChange, UserGroupEntityType, group?.Key)
                {
                    Field = $"permissions[{permission.EntityId.ToString(CultureInfo.InvariantCulture)}]",
                    NewValue = Join(permission.AssignedPermissions),
                });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public void Handle(UserSavingNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            var before = new Dictionary<Guid, ContentSnapshot?>();
            foreach (var user in notification.SavedEntities)
            {
                before[user.Key] = user.HasIdentity && userService.GetUserById(user.Id) is { } stored ? Groups(stored) : null;
            }

            notification.State[BeforeStateKey] = before;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    public void Handle(UserSavedNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Safely(() =>
        {
            if (!notification.State.TryGetValue(BeforeStateKey, out var state) || state is not Dictionary<Guid, ContentSnapshot?> before)
            {
                return;
            }

            foreach (var user in notification.SavedEntities)
            {
                if (before.TryGetValue(user.Key, out var snapshot))
                {
                    WriteChanges(UserEntityType, user.Key, snapshot ?? ContentSnapshot.Empty, Groups(user));
                }
            }
        });
    }

    private void WriteChanges(string entityType, Guid key, ContentSnapshot before, ContentSnapshot after)
    {
        foreach (var change in ContentSnapshot.Diff(before, after))
        {
            auditWriter.Write(new AuditRecord(AuditActions.PermissionChange, entityType, key)
            {
                Field = change.Field,
                OldValue = change.OldValue,
                NewValue = change.NewValue,
            });
        }
    }

    private static ContentSnapshot Snapshot(IUserGroup group) => ContentSnapshot.FromValues(new Dictionary<string, string?>
    {
        [ContentSnapshot.NameField] = group.Name,
        ["alias"] = group.Alias,
        ["sections"] = Join(group.AllowedSections),
        ["permissions"] = Join(group.Permissions),
        ["granularPermissions"] = Join(group.GranularPermissions.Select(p => JsonSerializer.Serialize((object)p))),
        ["startContentId"] = group.StartContentId?.ToString(CultureInfo.InvariantCulture),
        ["startMediaId"] = group.StartMediaId?.ToString(CultureInfo.InvariantCulture),
        ["hasAccessToAllLanguages"] = group.HasAccessToAllLanguages ? "1" : "0",
        ["languages"] = Join(group.AllowedLanguages.Select(l => l.ToString(CultureInfo.InvariantCulture))),
    });

    private static ContentSnapshot Groups(IUser user) =>
        ContentSnapshot.FromValues([new("groups", Join(user.Groups.Select(g => g.Alias)))]);

    private static string Join(IEnumerable<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));

    private void Safely(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Compliance audit of a permission change failed; the change itself has completed")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception);
}
