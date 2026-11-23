using Limescape.Umbraco.Compliance.Retention;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Api;

/// <summary>
/// Authorization for managing retention: administrators, plus the user groups in <see cref="RetentionOptions.ManagerGroups"/>.
/// </summary>
public static class RetentionPolicies
{
    /// <summary>Policy: may manage retention classes and assignments.</summary>
    public const string Manage = CompliancePackage.Id + ".Retention.Manage";

    /// <summary>True when <paramref name="user"/> may manage retention.</summary>
    public static bool CanManage(IUser user, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return user.IsAdmin() || user.Groups.Any(g => options.ManagerGroups.Contains(g.Alias, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Requirement behind <see cref="RetentionPolicies.Manage"/>.</summary>
public sealed class RetentionManagerRequirement : IAuthorizationRequirement;

/// <summary>Checks <see cref="RetentionManagerRequirement"/> against the authenticated Umbraco user (backoffice or API user).</summary>
public sealed class RetentionManagerHandler(IAuthorizationHelper authorizationHelper, IOptionsMonitor<RetentionOptions> options)
    : AuthorizationHandler<RetentionManagerRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RetentionManagerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (authorizationHelper.TryGetUmbracoUser(context.User, out var user) && RetentionPolicies.CanManage(user, options.CurrentValue))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
