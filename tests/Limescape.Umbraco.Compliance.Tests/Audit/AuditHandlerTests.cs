using System.Net;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.Audit;

/// <summary>
/// Every audited operation writes an entry with the right person, channel and technical
/// identity. The Management API rows carry an acting-user token, as an integration sends it; the service rows run
/// without HTTP context, as a job would.
/// </summary>
public sealed class AuditHandlerTests(UmbracoTestSite site)
{
    private const string Person = "auditor-1";
    private const string PersonName = "Aud Itor";

    private static readonly string[] DocumentsOnly = ["document"];

    private static string Token => TestIssuer.Shared.CreateToken(subject: Person, name: PersonName, channel: "app");

    private IContentService ContentService => site.Services.GetRequiredService<IContentService>();

    [Fact]
    public async Task Create_and_update_through_the_management_api_are_audited_per_field_with_the_acting_person()
    {
        var bed = await GateTestBed.GetAsync(site);
        var key = Guid.NewGuid();

        using (var created = await bed.Editor.SendAsync(HttpMethod.Post, "/document", Document(key, "Audit me", "First"), Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var updated = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{key}", Values("Audit me", "Second"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        var entries = ComplianceLogs.AuditFor(site, key);
        Assert.All(entries, e =>
        {
            Assert.Equal((Person, PersonName, "app", TestIssuer.DefaultIssuer), (e.ActorId, e.ActorName, e.Channel, e.Issuer));
            Assert.Equal(bed.Editor.UserKey, e.TechnicalUserKey);
        });
        Assert.Contains(entries, e => e is { Action: AuditActions.Create, Field: null } && e.NewValue!.Contains(GateTestBed.OpenType, StringComparison.Ordinal));
        Assert.Contains(entries, e => e is { Action: AuditActions.Create, Field: ContentSnapshot.NameField, NewValue: "Audit me" });
        Assert.Contains(entries, e => e is { Action: AuditActions.Create, Field: "title", OldValue: null, NewValue: "First" });
        var update = Assert.Single(entries, e => e.Action == AuditActions.Update);
        Assert.Equal(("title", "First", "Second"), (update.Field, update.OldValue, update.NewValue));
    }

    [Fact]
    public async Task A_save_without_changes_writes_no_update_entry()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.OpenType, "Same");

        Assert.True(ContentService.Save(ContentService.GetById(draft.Key)!).Success);

        Assert.DoesNotContain(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.Update);
    }

    [Fact]
    public async Task Long_values_are_truncated_to_2000_characters_in_the_field_diff()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.OpenType, "Short");
        var content = ContentService.GetById(draft.Key)!;
        content.SetValue("title", new string('x', 3000));
        Assert.True(ContentService.Save(content).Success);

        var update = Assert.Single(ComplianceLogs.AuditFor(site, draft.Key), e => e.Action == AuditActions.Update);
        Assert.Equal("Short", update.OldValue);
        Assert.Equal(AuditValues.MaxLength, update.NewValue!.Length);
        Assert.EndsWith(AuditValues.TruncationMarker, update.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_unpublish_and_move_through_the_management_api_are_audited()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.OpenType, "Movable");

        using (var published = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{draft.Key}/publish", new { publishSchedules = new[] { new { culture = (string?)null } } }, Token))
        {
            Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var unpublished = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{draft.Key}/unpublish", new { cultures = (string[]?)null }, Token))
        {
            Assert.True(unpublished.IsSuccessStatusCode, await unpublished.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // The open type only allows the gated type below it: move a gated child from one open parent to another.
        var from = bed.CreateDraft(GateTestBed.OpenType, "From");
        var to = bed.CreateDraft(GateTestBed.OpenType, "To");
        var child = bed.CreateDraft(GateTestBed.GatedType, "Child", from.Id);
        using (var moved = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{child.Key}/move", new { target = new { id = to.Key } }, Token))
        {
            Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var entries = ComplianceLogs.AuditFor(site, draft.Key).Where(e => e.ActorId == Person).ToList();
        Assert.Contains(entries, e => e.Action == AuditActions.Publish);
        Assert.Contains(entries, e => e.Action == AuditActions.Unpublish);
        var move = Assert.Single(ComplianceLogs.AuditFor(site, child.Key), e => e.Action == AuditActions.Move);
        Assert.Equal((Person, ContentAuditHandler.ParentField, from.Key.ToString(), to.Key.ToString()), (move.ActorId, move.Field, move.OldValue, move.NewValue));
    }

    [Fact]
    public async Task Trash_restore_trash_and_purge_give_four_deletion_log_entries_with_who_and_when()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = bed.CreateDraft(GateTestBed.OpenType, "Back and forth");
        var started = DateTime.UtcNow.AddSeconds(-1);

        await TrashAsync(bed, draft.Key);
        using (var restored = await bed.Editor.SendAsync(HttpMethod.Put, $"/recycle-bin/document/{draft.Key}/restore", new { target = (object?)null }, Token))
        {
            Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await TrashAsync(bed, draft.Key);
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        admin.DefaultRequestHeaders.Add("X-Limescape-Acting-User", TestIssuer.Shared.CreateToken(subject: "manager-1", name: "Mia Manager", channel: "job"));
        using (var purged = await admin.PostJsonAsync("/compliance/purge", new { restorationPeriodDays = 0, entityTypes = DocumentsOnly }))
        {
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        }

        Assert.Null(ContentService.GetById(draft.Key));
        var log = ComplianceLogs.DeletionLogFor(site, draft.Key);
        Assert.Equal(
            [DeletionLogEvents.Trashed, DeletionLogEvents.Restored, DeletionLogEvents.Trashed, DeletionLogEvents.Purged],
            log.Select(e => e.Event));
        Assert.All(log.Take(3), e => Assert.Equal((Person, "app"), (e.ActorId, e.Channel)));
        Assert.Equal(("manager-1", "job", PurgeReasons.RestorationPeriodEnded), (log[3].ActorId, log[3].Channel, log[3].Reason));
        Assert.All(log, e => Assert.InRange(e.TimestampUtc, started, DateTime.UtcNow.AddSeconds(1)));
        Assert.All(log, e => Assert.Equal(draft.Name, e.TitleSnapshot));

        var audit = ComplianceLogs.AuditFor(site, draft.Key);
        Assert.Equal(2, audit.Count(e => e.Action == AuditActions.Trash));
        Assert.Single(audit, e => e.Action == AuditActions.Restore);
        Assert.Single(audit, e => e is { Action: AuditActions.Delete, NewValue: PurgeReasons.RestorationPeriodEnded });
    }

    [Fact]
    public async Task Deleting_an_item_whose_retention_ended_records_the_retention_class_in_the_deletion_log()
    {
        // Retention has ended and there is no hold, so deletion succeeds and the class is logged.
        var type = await TestContent.CreateDocumentTypeAsync(site, "Ended retention");
        var name = "Old contract " + Guid.NewGuid().ToString("N")[..6];
        var item = TestContent.CreateDocument(site, type, name, (TestContent.ContractEnd, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var retention = site.Services.GetRequiredService<IRetentionService>();
        var className = "Contracts " + Guid.NewGuid().ToString("N")[..6];
        var cls = retention.CreateClass(new RetentionClassInput(className, new(1, 0, 0), RetentionStartEvent.DateProperty, RetentionEndAction.AllowDeletion) { StartPropertyAlias = TestContent.ContractEnd });
        retention.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.Item) { ItemKey = item.Key });
        Assert.Equal(RetentionStatus.Ended, retention.GetItem(item.Key)!.Status);

        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using (var deleted = await admin.DeletePathAsync($"/document/{item.Key}"))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var entry = Assert.Single(ComplianceLogs.DeletionLogFor(site, item.Key));
        Assert.Equal((DeletionLogEvents.Deleted, className, "api"), (entry.Event, entry.RetentionClass, entry.Channel));
        Assert.Contains(ComplianceLogs.AuditFor(site, item.Key), e => e.Action == AuditActions.Delete && e.OldValue == name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Media_create_update_and_trash_by_a_job_are_audited_as_system(bool duplicateSiblingName)
    {
        var mediaService = site.Services.GetRequiredService<IMediaService>();
        var requestedName = "Folder " + Guid.NewGuid().ToString("N");
        var renamedName = "Renamed " + requestedName;
        if (duplicateSiblingName)
        {
            // Reproduce the shared-site interference: another test can leave a sibling called "Folder".
            var sibling = mediaService.CreateMedia(requestedName, Constants.System.Root, Constants.Conventions.MediaTypes.Folder);
            Assert.True(mediaService.Save(sibling).Success);
        }

        var media = mediaService.CreateMedia(requestedName, Constants.System.Root, Constants.Conventions.MediaTypes.Folder);
        Assert.True(mediaService.Save(media).Success);
        // Umbraco makes sibling media names unique during persistence, before the saved notification.
        var savedName = media.Name;
        Assert.Equal(duplicateSiblingName ? requestedName + " (1)" : requestedName, savedName);
        media.Name = renamedName;
        Assert.True(mediaService.Save(media).Success);
        Assert.True(mediaService.MoveToRecycleBin(media).Success);

        var entries = ComplianceLogs.AuditFor(site, media.Key);
        Assert.All(entries, e =>
        {
            Assert.Equal(("system", "system"), (e.ActorId, e.Channel));
            Assert.Equal(AuditEntityTypes.Media, e.EntityType);
        });
        // A create has an entity row and one row per filled field; these are not duplicate notifications.
        Assert.Single(entries, e => e is { Action: AuditActions.Create, Field: null });
        var createdName = Assert.Single(entries, e => e is { Action: AuditActions.Create, Field: ContentSnapshot.NameField });
        Assert.Equal((null, savedName), (createdName.OldValue, createdName.NewValue));
        var update = Assert.Single(entries, e => e.Action == AuditActions.Update);
        Assert.Equal((ContentSnapshot.NameField, savedName, renamedName), (update.Field, update.OldValue, update.NewValue));
        Assert.Single(entries, e => e.Action == AuditActions.Trash);
        var deletion = Assert.Single(ComplianceLogs.DeletionLogFor(site, media.Key));
        Assert.Equal((DeletionLogEvents.Trashed, renamedName, "system", "system"),
            (deletion.Event, deletion.TitleSnapshot, deletion.ActorId, deletion.Channel));
    }

    [Fact]
    public async Task Changing_the_sections_and_permissions_of_a_user_group_is_audited_as_a_permission_change()
    {
        var userGroupService = site.Services.GetRequiredService<IUserGroupService>();
        var group = new UserGroup(site.Services.GetRequiredService<IShortStringHelper>())
        {
            Alias = "auditGroup" + Guid.NewGuid().ToString("N")[..6],
            Name = "Audit group",
        };
        group.AddAllowedSection(Constants.Applications.Content);
        Assert.True((await userGroupService.CreateAsync(group, Constants.Security.SuperUserKey)).Success);

        var stored = (await userGroupService.GetAsync(group.Key))!;
        stored.AddAllowedSection(Constants.Applications.Media);
        stored.Permissions = new HashSet<string> { "Umb.Document.Read" };
        Assert.True((await userGroupService.UpdateAsync(stored, Constants.Security.SuperUserKey)).Success);

        var entries = ComplianceLogs.AuditFor(site, group.Key).Where(e => e.Action == AuditActions.PermissionChange).ToList();
        Assert.All(entries, e => Assert.Equal(PermissionAuditHandler.UserGroupEntityType, e.EntityType));
        Assert.Contains(entries, e => e is { Field: "sections", OldValue: Constants.Applications.Content } && e.NewValue == $"{Constants.Applications.Content},{Constants.Applications.Media}");
        Assert.Contains(entries, e => e is { Field: "permissions", NewValue: "Umb.Document.Read" });
    }

    [Fact]
    public async Task Adding_a_user_to_a_group_is_audited_as_a_permission_change_of_the_user()
    {
        var userService = site.Services.GetRequiredService<IUserService>();
        var userGroupService = site.Services.GetRequiredService<IUserGroupService>();
        var editors = (await userGroupService.GetAsync("editor"))!;
        var writers = (await userGroupService.GetAsync("writer"))!;
        var email = $"audit-{Guid.NewGuid():N}@example.test";
        var created = await userService.CreateAsync(
            Constants.Security.SuperUserKey,
            new UserCreateModel { Email = email, UserName = email, Name = "Audit user", Kind = UserKind.Default, UserGroupKeys = new HashSet<Guid> { editors.Key } },
            approveUser: true);
        Assert.True(created.Success, created.Status.ToString());
        var user = userService.GetUserById(created.Result.CreatedUser!.Id)!;

        user.AddGroup(writers.ToReadOnlyGroup());
        userService.Save(user);

        Assert.Contains(ComplianceLogs.AuditFor(site, user.Key), e => e is { Action: AuditActions.PermissionChange, Field: "groups", OldValue: "editor", NewValue: "editor,writer" });
    }

    [Fact]
    public void Field_diff_ignores_empty_to_empty_and_reports_added_and_removed_fields()
    {
        var before = ContentSnapshot.FromValues([new("a", "1"), new("b", null), new("c", "x")]);
        var after = ContentSnapshot.FromValues([new("a", "2"), new("b", ""), new("d", "y")]);

        var changes = ContentSnapshot.Diff(before, after);

        Assert.Equal(
            [new FieldChange("a", "1", "2"), new FieldChange("c", "x", null), new FieldChange("d", null, "y")],
            changes);
    }

    private static async Task TrashAsync(GateTestBed bed, Guid key)
    {
        using var trashed = await bed.Editor.SendAsync(HttpMethod.Put, $"/document/{key}/move-to-recycle-bin", null, Token);
        Assert.True(trashed.IsSuccessStatusCode, await trashed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private object Document(Guid key, string name, string title) => new
    {
        id = key,
        documentType = new { id = site.Services.GetRequiredService<IContentTypeService>().Get(GateTestBed.OpenType)!.Key },
        parent = (object?)null,
        template = (object?)null,
        values = new[] { new { alias = "title", culture = (string?)null, segment = (string?)null, value = (object?)title } },
        variants = new[] { new { culture = (string?)null, segment = (string?)null, name } },
    };

    private static object Values(string name, string title) => new
    {
        template = (object?)null,
        values = new[] { new { alias = "title", culture = (string?)null, segment = (string?)null, value = (object?)title } },
        variants = new[] { new { culture = (string?)null, segment = (string?)null, name } },
    };
}
