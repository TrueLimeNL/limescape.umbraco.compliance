using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Audit;

/// <summary>
/// Purge of the recycle bin after a restoration period, through <c>POST compliance/purge</c>. The negative tests
/// put items under retention or legal hold after they were trashed (the only way such an item can be in the bin).
/// </summary>
public sealed class PurgeTests(UmbracoTestSite site)
{
    private IContentService ContentService => site.Services.GetRequiredService<IContentService>();

    private IRetentionService Retention => site.Services.GetRequiredService<IRetentionService>();

    [Fact]
    public async Task Purge_through_the_api_never_deletes_an_item_whose_retention_period_runs()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Purge retained");
        var retained = Trashed(TestContent.CreateDocument(site, type, "Retained in bin"));
        // Retention starts after the item was trashed: a 10-year class from the last modification.
        var cls = Retention.CreateClass(new RetentionClassInput("Purge test " + Guid.NewGuid().ToString("N")[..6], new(10, 0, 0), RetentionStartEvent.LastModified, RetentionEndAction.Notify));
        Retention.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.Item) { ItemKey = retained.Key });
        Assert.Equal(RetentionStatus.Active, Retention.GetItem(retained.Key)!.Status);

        var result = await PurgeAsync(0);

        var skipped = Skipped(result, retained.Key);
        Assert.Equal(DeletionBlockErrorCodes.RetentionBlocksDeletion, skipped.GetProperty("reason").GetString());
        Assert.Equal("retention", skipped.GetProperty("blockedItems")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain(result.GetProperty("purged").EnumerateArray(), p => p.GetProperty("key").GetGuid() == retained.Key);
        Assert.True(ContentService.GetById(retained.Key)!.Trashed);
        Assert.DoesNotContain(ComplianceLogs.DeletionLogFor(site, retained.Key), e => e.Event is DeletionLogEvents.Purged or DeletionLogEvents.Deleted);
        Assert.Contains(ComplianceLogs.AuditFor(site, retained.Key), e => e is { Action: AuditActions.DeleteBlocked, Field: "purge" });
    }

    [Fact]
    public async Task Purge_skips_an_item_on_legal_hold_and_a_folder_with_a_held_descendant()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Purge held");
        var held = Trashed(TestContent.CreateDocument(site, type, "Held in bin"));
        var folder = TestContent.CreateDocument(site, type, "Folder in bin");
        var child = ContentService.Create("Held child", folder.Id, type);
        Assert.True(ContentService.Save(child).Success);
        Trashed(folder);
        var blocks = site.Services.GetRequiredService<IDeletionBlockService>();
        Assert.NotNull(blocks.SetLegalHold(held.Key, legalHold: true, reason: "case 7"));
        Assert.NotNull(blocks.SetLegalHold(child.Key, legalHold: true, reason: "case 7"));

        var result = await PurgeAsync(0);

        Assert.Equal("legalHold", Skipped(result, held.Key).GetProperty("blockedItems")[0].GetProperty("reason").GetString());
        Assert.Equal(child.Key, Skipped(result, folder.Key).GetProperty("blockedItems")[0].GetProperty("itemKey").GetGuid());
        Assert.NotNull(ContentService.GetById(held.Key));
        Assert.NotNull(ContentService.GetById(folder.Key));
        Assert.NotNull(ContentService.GetById(child.Key));
    }

    [Fact]
    public async Task Purge_deletes_free_items_older_than_the_restoration_period_and_keeps_newer_ones()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Purge free");
        var free = Trashed(TestContent.CreateDocument(site, type, "Free in bin"));

        var notYet = await PurgeAsync(30);
        Assert.NotNull(ContentService.GetById(free.Key));
        Assert.True(notYet.GetProperty("notDue").GetInt32() >= 1);

        var dryRun = await PurgeAsync(0, dryRun: true);
        Assert.Contains(dryRun.GetProperty("purged").EnumerateArray(), p => p.GetProperty("key").GetGuid() == free.Key);
        Assert.NotNull(ContentService.GetById(free.Key));
        Assert.DoesNotContain(ComplianceLogs.DeletionLogFor(site, free.Key), e => e.Event == DeletionLogEvents.Purged);

        var result = await PurgeAsync(0);
        Assert.Contains(result.GetProperty("purged").EnumerateArray(), p => p.GetProperty("key").GetGuid() == free.Key);
        Assert.Null(ContentService.GetById(free.Key));
        var purged = Assert.Single(ComplianceLogs.DeletionLogFor(site, free.Key), e => e.Event == DeletionLogEvents.Purged);
        Assert.Equal(PurgeReasons.RestorationPeriodEnded, purged.Reason);
    }

    [Fact]
    public async Task Purge_removes_media_from_the_media_recycle_bin()
    {
        var mediaService = site.Services.GetRequiredService<IMediaService>();
        var media = mediaService.CreateMedia("Old folder", Constants.System.Root, Constants.Conventions.MediaTypes.Folder);
        Assert.True(mediaService.Save(media).Success);
        Assert.True(mediaService.MoveToRecycleBin(media).Success);

        var result = await PurgeAsync(0, entityTypes: ["media"]);

        Assert.Contains(result.GetProperty("purged").EnumerateArray(), p => p.GetProperty("key").GetGuid() == media.Key);
        Assert.Null(mediaService.GetById(media.Key));
        Assert.Equal(AuditEntityTypes.Media, Assert.Single(ComplianceLogs.DeletionLogFor(site, media.Key), e => e.Event == DeletionLogEvents.Purged).EntityType);
    }

    [Fact]
    public async Task Purge_needs_a_retention_manager_and_a_restoration_period()
    {
        using var editor = await ManagementApi.ClientAsync(site, "editor");
        using (var forbidden = await editor.PostJsonAsync("/compliance/purge", new { restorationPeriodDays = 0 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var invalid = await admin.PostJsonAsync("/compliance/purge", new { dryRun = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("purge_invalid", (await invalid.JsonAsync()).GetProperty("code").GetString());
    }

    private IContent Trashed(IContent content)
    {
        Assert.True(ContentService.MoveToRecycleBin(content).Success);
        return content;
    }

    private async Task<JsonElement> PurgeAsync(int days, bool dryRun = false, string[]? entityTypes = null)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var response = await admin.PostJsonAsync("/compliance/purge", new { restorationPeriodDays = days, dryRun, entityTypes = entityTypes ?? ["document"] });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Skipped(JsonElement result, Guid key) =>
        Assert.Single(result.GetProperty("skipped").EnumerateArray(), s => s.GetProperty("key").GetGuid() == key);
}
