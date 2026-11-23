using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Api;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.FieldState;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Purge;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Submission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Composers;

/// <summary>
/// Entry point Umbraco discovers when the package is installed. The package migration plan is discovered by type.
/// Audit, retention, deletion block, purge, submission guard, publication gate and lifecycle routes are registered here.
/// </summary>
public sealed class ComplianceComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<ActingUserOptions>().Bind(builder.Config.GetSection(ActingUserOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddSingleton<IActingUserTokenValidator, ActingUserTokenValidator>();
        builder.Services.TryAddSingleton<IActingUserAccessor, ActingUserAccessor>();
        builder.Services.TryAddSingleton<IAuditWriter, AuditWriter>();
        builder.Services.TryAddSingleton<IDeletionLogWriter, DeletionLogWriter>();

        // Audit handlers, query API and log retention. Registered before the retention handlers on
        // purpose: Umbraco runs the handlers of one notification in registration order, and the deletion log must read
        // an item's retention class before RetentionRecalculationHandler forgets it on ContentDeleted/MediaDeleted.
        builder.Services.AddOptions<AuditOptions>().Bind(builder.Config.GetSection(AuditOptions.SectionName));
        builder.Services.TryAddSingleton<IAuditLogReader, AuditLogReader>();
        builder.Services.AddRecurringBackgroundJob<LogRetentionJob>();
        builder
            .AddNotificationHandler<ContentSavingNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentSavedNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentPublishedNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentUnpublishedNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentMovedNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentMovedToRecycleBinNotification, ContentAuditHandler>()
            .AddNotificationHandler<ContentDeletedNotification, ContentAuditHandler>()
            .AddNotificationHandler<MediaSavingNotification, ContentAuditHandler>()
            .AddNotificationHandler<MediaSavedNotification, ContentAuditHandler>()
            .AddNotificationHandler<MediaMovedNotification, ContentAuditHandler>()
            .AddNotificationHandler<MediaMovedToRecycleBinNotification, ContentAuditHandler>()
            .AddNotificationHandler<MediaDeletedNotification, ContentAuditHandler>()
            .AddNotificationHandler<RelationSavedNotification, ContentAuditHandler>()
            .AddNotificationHandler<RelationDeletedNotification, ContentAuditHandler>()
            .AddNotificationAsyncHandler<UserGroupSavingNotification, PermissionAuditHandler>()
            .AddNotificationAsyncHandler<UserGroupSavedNotification, PermissionAuditHandler>()
            .AddNotificationAsyncHandler<UserGroupDeletedNotification, PermissionAuditHandler>()
            .AddNotificationAsyncHandler<UserGroupWithUsersSavedNotification, PermissionAuditHandler>()
            .AddNotificationAsyncHandler<AssignedUserGroupPermissionsNotification, PermissionAuditHandler>()
            .AddNotificationHandler<UserSavingNotification, PermissionAuditHandler>()
            .AddNotificationHandler<UserSavedNotification, PermissionAuditHandler>();

        // Retention.
        builder.Services.AddOptions<RetentionOptions>().Bind(builder.Config.GetSection(RetentionOptions.SectionName));
        builder.Services.TryAddSingleton<IRetentionService, RetentionService>();
        builder.Services.AddSingleton<IAuthorizationHandler, RetentionManagerHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            RetentionPolicies.Manage,
            policy => policy.AddRequirements(new RetentionManagerRequirement())));
        builder.Services.AddRecurringBackgroundJob<RetentionJob>();
        builder
            .AddNotificationHandler<ContentSavedNotification, RetentionRecalculationHandler>()
            .AddNotificationHandler<ContentPublishedNotification, RetentionRecalculationHandler>()
            .AddNotificationHandler<MediaSavedNotification, RetentionRecalculationHandler>()
            .AddNotificationHandler<ContentDeletedNotification, RetentionRecalculationHandler>()
            .AddNotificationHandler<MediaDeletedNotification, RetentionRecalculationHandler>();

        // Deletion block and legal hold.
        builder.Services.TryAddSingleton<IDeletionBlockService, DeletionBlockService>();
        builder.Services.Configure<MvcOptions>(options => options.Filters.Add<DeletionBlockResultFilter>());
        builder
            .AddNotificationHandler<ContentMovingToRecycleBinNotification, DeletionBlockHandler>()
            .AddNotificationHandler<ContentDeletingNotification, DeletionBlockHandler>()
            .AddNotificationHandler<ContentEmptyingRecycleBinNotification, DeletionBlockHandler>()
            .AddNotificationHandler<MediaMovingToRecycleBinNotification, DeletionBlockHandler>()
            .AddNotificationHandler<MediaDeletingNotification, DeletionBlockHandler>()
            .AddNotificationHandler<MediaEmptyingRecycleBinNotification, DeletionBlockHandler>()
            .AddNotificationHandler<ContentTypeDeletingNotification, DeletionBlockHandler>()
            .AddNotificationHandler<MediaTypeDeletingNotification, DeletionBlockHandler>();
        // Purge of the recycle bin after the restoration period.
        builder.Services.AddOptions<PurgeOptions>().Bind(builder.Config.GetSection(PurgeOptions.SectionName));
        builder.Services.TryAddSingleton<IMaterialElements, MaterialElements>();
        builder.Services.TryAddSingleton<IPurgeService, PurgeService>();
        builder
            .AddNotificationHandler<ContentMovedToRecycleBinNotification, ElementsFollowMaterialHandler>()
            .AddNotificationHandler<ContentMovedNotification, ElementsFollowMaterialHandler>();

        // Submission guard: required fields from configuration, the Umbraco mandatory flag and a schema snapshot; integrations add providers.
        builder.Services.AddOptions<SubmissionOptions>().Bind(builder.Config.GetSection(SubmissionOptions.SectionName));
        builder.Services.AddSingleton<IRequiredFieldProvider, ConfiguredRequiredFieldProvider>();
        builder.Services.AddSingleton<IRequiredFieldProvider, MandatoryPropertyRequiredFieldProvider>();
        builder.Services.AddSingleton<IRequiredFieldProvider, SchemaSnapshotRequiredFieldProvider>();
        builder.Services.TryAddSingleton<ISubmissionGuard, SubmissionGuard>();
        builder.AddNotificationHandler<ContentSavingNotification, SubmissionGuardHandler>();

        // Archived fields and deprecated codes: no new value on any route that saves.
        builder.Services.AddOptions<FieldStateOptions>().Bind(builder.Config.GetSection(FieldStateOptions.SectionName));
        builder.Services.TryAddSingleton<IFieldStateStore, KeyValueFieldStateStore>();
        builder.Services.TryAddSingleton<IFieldStateService, FieldStateService>();
        builder.AddNotificationHandler<ContentSavingNotification, FieldStateGuardHandler>();

        // Publication gate.
        builder.Services.AddOptions<PublicationGateOptions>().Bind(builder.Config.GetSection(PublicationGateOptions.SectionName));
        builder.Services.TryAddSingleton<IGatedContentTypes, GatedContentTypes>();
        builder.Services.TryAddSingleton<IContentVersionHasher, ContentVersionHasher>();
        builder.Services.TryAddSingleton<IApprovalStore, ApprovalStore>();
        builder.Services.TryAddSingleton<IApprovalService, ApprovalService>();
        builder.Services.TryAddSingleton<IWorkflowApprovalTracker, WorkflowApprovalTracker>();
        builder.Services.TryAddSingleton<WorkflowApprovalHandler>();
        builder.AddNotificationHandler<ContentPublishingNotification, PublicationGateHandler>()
            .AddNotificationHandler<ContentSavingNotification, PublicationStatusHandler>()
            .AddNotificationHandler<ContentUnpublishingNotification, PublicationStatusHandler>();

        // Lifecycle: rejection and archive move protected statuses only through their own routes.
        builder.Services.TryAddSingleton<IArchiveService, ArchiveService>();
        builder.AddNotificationHandler<ContentSavingNotification, LifecycleGuardHandler>();
        // Only when Umbraco Workflow is installed; the package does not reference it.
        WorkflowApprovalHandler.Register(builder.Services);

        // First in the Umbraco pipeline, so an invalid acting-user token is rejected before any endpoint runs.
        builder.Services.Configure<UmbracoPipelineOptions>(pipeline => pipeline.AddFilter(
            new UmbracoPipelineFilter(CompliancePackage.Id + ".ActingUser")
            {
                PrePipeline = app => app.UseMiddleware<ActingUserMiddleware>(),
            }));
    }
}
