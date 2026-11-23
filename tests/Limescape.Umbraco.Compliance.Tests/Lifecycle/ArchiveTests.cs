using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Lifecycle;
using Limescape.Umbraco.Compliance.PublicationGate;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core.Models;

namespace Limescape.Umbraco.Compliance.Tests.Lifecycle;

/// <summary>
/// The archive route through the Management API, as an integration or the backoffice calls it, and the rules that
/// protect it. Every request bypasses the UI.
/// </summary>
public sealed class ArchiveTests(UmbracoTestSite site)
{
    private const string Owner = "archive-owner";

    [Fact]
    public async Task Owner_archives_a_published_material_status_date_unpublish_audit_and_retention_start_in_one_step()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        var retention = site.Services.GetRequiredService<IRetentionService>();
        var sevenYears = retention.CreateClass(new RetentionClassInput($"7 years after archive {Guid.NewGuid():N}", new RetentionDuration(7, 0, 0), RetentionStartEvent.Archived, RetentionEndAction.AllowDeletion));
        retention.CreateAssignment(new RetentionAssignment(0, sevenYears.Key, RetentionAssignmentTarget.Item) { ItemKey = draft.Key });
        Assert.True(retention.GetItem(draft.Key)!.OpenEnded);

        using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}",
            new { reason = "Vervangen door nieuwe richtlijn" }, OwnerToken());

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = JsonDocument.Parse(body).RootElement;
        Assert.True(state.GetProperty("archived").GetBoolean());
        Assert.False(state.GetProperty("published").GetBoolean());

        var stored = bed.Reload(draft.Key);
        Assert.False(stored.Published);
        Assert.Equal(LifecycleStatuses.Archived, stored.GetValue<string>("status"));
        var archivedAt = RetentionCalculator.ParseDate(stored.GetValue("archivedAt"));
        Assert.NotNull(archivedAt);
        Assert.InRange(archivedAt!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));

        var item = retention.GetItem(draft.Key)!;
        Assert.False(item.OpenEnded);
        Assert.Equal(RetentionStatus.Active, item.Status);
        Assert.Equal(archivedAt.Value.Date, item.StartDate!.Value.Date);
        Assert.Equal(archivedAt.Value.AddYears(7).Date, item.RetentionUntil!.Value.Date);

        var audit = ComplianceLogs.AuditFor(site, draft.Key);
        Assert.Contains(audit, a => a.Action == AuditActions.Archive && a.NewValue == "Vervangen door nieuwe richtlijn" && a.ActorId == Owner);
        Assert.Contains(audit, a => a.Action == AuditActions.Unpublish && a.ActorId == Owner);
        Assert.Contains(audit, a => a.Action == AuditActions.Update && a.Field == "status" && a.NewValue == "archived" && a.ActorId == Owner);
    }

    [Fact]
    public async Task The_running_list_of_the_retention_dashboard_shows_start_and_end_of_an_archived_material()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        var retention = site.Services.GetRequiredService<IRetentionService>();
        var tenYears = retention.CreateClass(new RetentionClassInput($"10 years after archive {Guid.NewGuid():N}", new RetentionDuration(10, 0, 0), RetentionStartEvent.Archived, RetentionEndAction.Notify));
        retention.CreateAssignment(new RetentionAssignment(0, tenYears.Key, RetentionAssignmentTarget.Item) { ItemKey = draft.Key });
        using (var archived = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { }, OwnerToken()))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var running = await admin.GetPathAsync("/compliance/retention/running?take=500");
        using var editor = await ManagementApi.ClientAsync(site, "editor");
        using var forbidden = await editor.GetPathAsync("/compliance/retention/running");

        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        var item = (await running.JsonAsync()).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemKey").GetGuid() == draft.Key);
        var start = item.GetProperty("startDate").GetDateTime();
        Assert.Equal(start.AddYears(10).Date, item.GetProperty("retentionUntil").GetDateTime().Date);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Archiving_by_someone_who_is_not_an_owner_is_refused_with_403()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);

        using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { },
            TestIssuer.Shared.CreateToken(subject: "not-the-owner", name: "Nora Other", channel: "app"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GateMessages.ArchiveNotOwner, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertStillPublished(bed, draft);
    }

    [Fact]
    public async Task Archiving_without_a_person_is_refused_with_403()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);

        using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GateMessages.ArchiveRequiresActingUser, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertStillPublished(bed, draft);
    }

    [Fact]
    public async Task Archiving_a_draft_is_refused_with_409()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = OwnedDraft(bed);

        using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { }, OwnerToken());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(GateMessages.ArchiveRequiresPublished, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var stored = bed.Reload(draft.Key);
        Assert.Equal("draft", stored.GetValue<string>("status"));
        Assert.Null(stored.GetValue("archivedAt"));
    }

    [Fact]
    public async Task Archiving_twice_is_refused_with_409_and_an_archived_material_cannot_be_published_again()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        using (var first = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { }, OwnerToken()))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { }, OwnerToken());
        // The approval of this exact version still exists; the gate refuses the archived document anyway.
        using var republish = await bed.Editor.PublishAsync(draft.Key);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(GateMessages.ArchiveAlreadyArchived, await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(republish.IsSuccessStatusCode);
        Assert.True(republish.Headers.TryGetValues("Umb-Notifications", out var notifications));
        Assert.Contains(GateMessages.PublicationArchived, string.Concat(notifications), StringComparison.Ordinal);
        Assert.False(bed.Reload(draft.Key).Published);
    }

    [Fact]
    public async Task A_functional_manager_archives_material_of_someone_else_and_the_state_endpoint_says_who_may()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var managerToken = TestIssuer.Shared.CreateToken(subject: "manager-1", name: "Mona Manager", channel: "app");

        using var ownerState = await bed.Editor.SendAsync(HttpMethod.Get, $"/compliance/archive/document/{draft.Key}", actingUserToken: OwnerToken());
        using var otherState = await bed.Editor.SendAsync(HttpMethod.Get, $"/compliance/archive/document/{draft.Key}",
            actingUserToken: TestIssuer.Shared.CreateToken(subject: "not-the-owner", channel: "app"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ManagementApi.Base + $"/compliance/archive/document/{draft.Key}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { reason = "Beheer: verouderd" }),
        };
        request.Headers.Add("X-Limescape-Acting-User", managerToken);
        using var archived = await admin.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True((await ownerState.JsonAsync()).GetProperty("canArchive").GetBoolean());
        Assert.False((await otherState.JsonAsync()).GetProperty("canArchive").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Contains(ComplianceLogs.AuditFor(site, draft.Key), a => a.Action == AuditActions.Archive && a.ActorId == "manager-1");
    }

    [Fact]
    public async Task Archive_date_and_status_cannot_be_set_in_code_or_through_the_management_api()
    {
        // Negative test: the choice for this rule is to refuse. In this test site the fields are text properties, so
        // the Management API passes the values on and the content layer refuses them (400). In the content layer host
        // they are labels, which Umbraco drops before saving (200, nothing changes).
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        var direct = bed.Reload(draft.Key);
        direct.SetValue("archivedAt", "2026-01-15T00:00:00Z");
        var dateInCode = bed.ContentService.Save(direct);
        var statusOnly = bed.Reload(draft.Key);
        statusOnly.SetValue("status", "archived");
        var statusInCode = bed.ContentService.Save(statusOnly);
        using var putDate = await admin.PutJsonAsync($"/document/{draft.Key}", RejectionTests.DocumentBody(draft, ("archivedAt", "2026-01-15T00:00:00Z")));
        using var putStatus = await admin.PutJsonAsync($"/document/{draft.Key}", RejectionTests.DocumentBody(draft, ("status", "archived")));

        Assert.False(dateInCode.Success);
        Assert.Contains(dateInCode.EventMessages!.GetAll(), m => m.Category == GateMessages.LifecycleArchiveRequiresRoute);
        Assert.False(statusInCode.Success);
        foreach (var put in new[] { putDate, putStatus })
        {
            Assert.False(put.IsSuccessStatusCode);
            Assert.True(put.Headers.TryGetValues("Umb-Notifications", out var notifications));
            Assert.Contains(GateMessages.LifecycleArchiveRequiresRoute, string.Concat(notifications), StringComparison.Ordinal);
        }

        AssertStillPublished(bed, draft);
        Assert.Null(bed.Reload(draft.Key).GetValue("archivedAt"));
    }

    [Fact]
    public async Task An_archived_material_cannot_be_reopened_by_changing_its_status()
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        using (var archived = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}", new { }, OwnerToken()))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        var reopened = bed.Reload(draft.Key);
        reopened.SetValue("status", "draft");
        var cleared = bed.Reload(draft.Key);
        cleared.SetValue("archivedAt", null);

        Assert.False(bed.ContentService.Save(reopened).Success);
        Assert.False(bed.ContentService.Save(cleared).Success);
        Assert.Equal(LifecycleStatuses.Archived, bed.Reload(draft.Key).GetValue<string>("status"));
    }

    [Theory]
    [InlineData(RetentionStartEvent.Archived)]
    [InlineData(RetentionStartEvent.Published)]
    public async Task Cancelled_unpublish_rolls_back_content_retention_and_all_audit_entries(RetentionStartEvent startEvent)
    {
        var bed = await GateTestBed.GetAsync(site);
        var draft = await PublishedAsync(bed);
        var retention = site.Services.GetRequiredService<IRetentionService>();
        var cls = retention.CreateClass(new RetentionClassInput($"Rollback {Guid.NewGuid():N}",
            new RetentionDuration(7, 0, 0), startEvent, RetentionEndAction.AllowDeletion));
        retention.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.Item) { ItemKey = draft.Key });
        var before = bed.Reload(draft.Key);
        var period = retention.GetItem(draft.Key)!;
        Assert.Equal(startEvent == RetentionStartEvent.Archived, period.OpenEnded);
        if (startEvent == RetentionStartEvent.Published)
        {
            Assert.NotNull(period.RetentionUntil);
        }
        var periodBefore = JsonSerializer.Serialize(period);
        var auditBefore = JsonSerializer.Serialize(ComplianceLogs.AuditFor(site, draft.Key));
        Assert.True(CancelArchiveUnpublishHandler.Targets.TryAdd(draft.Key, false));
        try
        {
            using var response = await bed.Editor.SendAsync(HttpMethod.Post, $"/compliance/archive/document/{draft.Key}",
                new { reason = "Must not be committed" }, OwnerToken());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("archive_cancelled", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(CancelArchiveUnpublishHandler.Targets[draft.Key]); // Proves we reached the unpublish handler.
            var after = bed.Reload(draft.Key);
            Assert.True(after.Published);
            Assert.Equal(before.GetValue<string>("status"), after.GetValue<string>("status"));
            Assert.Equal(before.GetValue("archivedAt"), after.GetValue("archivedAt"));
            Assert.Equal(periodBefore, JsonSerializer.Serialize(retention.GetItem(draft.Key)));
            Assert.Equal(auditBefore, JsonSerializer.Serialize(ComplianceLogs.AuditFor(site, draft.Key)));
        }
        finally
        {
            CancelArchiveUnpublishHandler.Targets.TryRemove(draft.Key, out _);
        }
    }
    private static string OwnerToken() => TestIssuer.Shared.CreateToken(subject: Owner, name: "Olga Owner", channel: "app");

    private static IContent OwnedDraft(GateTestBed bed)
    {
        var draft = bed.CreateDraft();
        draft.SetValue("owners", $"[\"{Owner}\",\"co-owner\"]");
        draft.SetValue("status", "draft");
        Assert.True(bed.ContentService.Save(draft).Success);
        return draft;
    }

    private static async Task<IContent> PublishedAsync(GateTestBed bed)
    {
        var draft = OwnedDraft(bed);
        await RejectionTests.ApproveAndPublishAsync(bed, draft);
        Assert.True(bed.Reload(draft.Key).Published);
        return draft;
    }

    private static void AssertStillPublished(GateTestBed bed, IContent draft)
    {
        var stored = bed.Reload(draft.Key);
        Assert.True(stored.Published);
        Assert.Equal(LifecycleStatuses.Published, stored.GetValue<string>("status"));
    }
}
