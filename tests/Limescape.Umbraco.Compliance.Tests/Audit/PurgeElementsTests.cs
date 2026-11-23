using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.Purge;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Limescape.Umbraco.Compliance.Tests.Audit;

/// <summary>
/// A purged document takes its elements (the media in its <c>elements</c> picker) along, unless another item
/// uses them or they are under retention or legal hold; trash and restore move the elements with the document. Every
/// element gets its own deletion-log row. Every lookup uses the keys of the items the test made, never a timestamp,
/// because the other tests leave items in the same site.
/// </summary>
public sealed class PurgeElementsTests(UmbracoTestSite site)
{
    private static readonly string[] DocumentOnly = ["document"];

    private IContentService ContentService => site.Services.GetRequiredService<IContentService>();

    private IMediaService MediaService => site.Services.GetRequiredService<IMediaService>();

    [Fact]
    public async Task Purge_through_the_api_deletes_the_elements_of_the_purged_material_and_logs_each_one()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Purge elements");
        var first = CreateElement();
        var second = CreateElement();
        var material = CreateMaterial(type, "Material with elements", first, second);
        Assert.True(ContentService.MoveToRecycleBin(material).Success);

        var dryRun = await PurgeAsync(0, dryRun: true);
        Assert.Equal(2, Elements(dryRun, material.Key).Count(e => e.GetProperty("purged").GetBoolean()));
        Assert.NotNull(MediaService.GetById(first.Key));
        Assert.DoesNotContain(ComplianceLogs.DeletionLogFor(site, first.Key), e => e.Event == DeletionLogEvents.Purged);

        var result = await PurgeAsync(0);

        var purgedKeys = Elements(result, material.Key).Where(e => e.GetProperty("purged").GetBoolean()).Select(e => e.GetProperty("key").GetGuid()).Order();
        Assert.Equal(new[] { first.Key, second.Key }.Order(), purgedKeys);
        Assert.Null(ContentService.GetById(material.Key));
        foreach (var element in new[] { first, second })
        {
            Assert.Null(MediaService.GetById(element.Key));
            var purged = Assert.Single(ComplianceLogs.DeletionLogFor(site, element.Key), e => e.Event == DeletionLogEvents.Purged);
            Assert.Equal(AuditEntityTypes.Media, purged.EntityType);
            Assert.Equal(PurgeReasons.ElementOf(material.Key), purged.Reason);
            Assert.Equal(element.Name, purged.TitleSnapshot);
        }
    }

    [Fact]
    public async Task Purge_keeps_an_element_that_another_material_still_uses_and_logs_the_skip()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Purge shared");
        var shared = CreateElement();
        var own = CreateElement();
        var purged = CreateMaterial(type, "Purged material", shared, own);
        var other = CreateMaterial(type, "Other material", shared);
        Assert.True(ContentService.MoveToRecycleBin(purged).Success);
        Assert.False(MediaService.GetById(shared.Key)!.Trashed, "an element used by a live material stays in the tree");
        Assert.True(MediaService.GetById(own.Key)!.Trashed);

        var result = await PurgeAsync(0);

        Assert.Null(ContentService.GetById(purged.Key));
        Assert.NotNull(MediaService.GetById(shared.Key));
        Assert.Null(MediaService.GetById(own.Key));
        var skipped = Assert.Single(Elements(result, purged.Key), e => e.GetProperty("key").GetGuid() == shared.Key);
        Assert.False(skipped.GetProperty("purged").GetBoolean());
        Assert.Equal(PurgeElementReasons.Shared, skipped.GetProperty("reason").GetString());
        var log = Assert.Single(ComplianceLogs.DeletionLogFor(site, shared.Key), e => e.Event == DeletionLogEvents.PurgeSkipped);
        Assert.Equal(PurgeReasons.ElementOf(purged.Key, PurgeElementReasons.Shared), log.Reason);
        Assert.DoesNotContain(ComplianceLogs.DeletionLogFor(site, shared.Key), e => e.Event is DeletionLogEvents.Purged or DeletionLogEvents.Deleted);

        // The other material is the last user once the first one is gone: purging it takes the element along.
        Assert.True(ContentService.MoveToRecycleBin(other).Success);
        Assert.True(MediaService.GetById(shared.Key)!.Trashed);
        await PurgeAsync(0);
        Assert.Null(MediaService.GetById(shared.Key));
    }

    [Fact]
    public async Task Purge_keeps_an_element_under_legal_hold_and_audits_the_refusal()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Purge held element");
        var held = CreateElement();
        var free = CreateElement();
        var material = CreateMaterial(type, "Material with held element", held, free);
        Assert.True(ContentService.MoveToRecycleBin(material).Success);
        Assert.True(MediaService.GetById(held.Key)!.Trashed);
        Assert.NotNull(site.Services.GetRequiredService<IDeletionBlockService>().SetLegalHold(held.Key, legalHold: true, reason: "case 10"));

        var result = await PurgeAsync(0);

        Assert.Null(ContentService.GetById(material.Key));
        Assert.NotNull(MediaService.GetById(held.Key));
        Assert.Null(MediaService.GetById(free.Key));
        var skipped = Assert.Single(Elements(result, material.Key), e => e.GetProperty("key").GetGuid() == held.Key);
        Assert.Equal(DeletionBlockErrorCodes.RetentionBlocksDeletion, skipped.GetProperty("reason").GetString());
        Assert.Equal("legalHold", skipped.GetProperty("blockedItems")[0].GetProperty("reason").GetString());
        Assert.Contains(ComplianceLogs.DeletionLogFor(site, held.Key), e => e.Event == DeletionLogEvents.PurgeSkipped && e.Reason == PurgeReasons.ElementOf(material.Key, DeletionBlockErrorCodes.RetentionBlocksDeletion));
        Assert.DoesNotContain(ComplianceLogs.DeletionLogFor(site, held.Key), e => e.Event is DeletionLogEvents.Purged or DeletionLogEvents.Deleted);
        Assert.Contains(ComplianceLogs.AuditFor(site, held.Key), e => e is { Action: AuditActions.DeleteBlocked, Field: "purge" });
    }

    [Fact]
    public async Task Trash_leaves_an_element_under_legal_hold_where_it_is()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Trash held element");
        var held = CreateElement();
        var free = CreateElement();
        var material = CreateMaterial(type, "Trashed with held element", held, free);
        Assert.NotNull(site.Services.GetRequiredService<IDeletionBlockService>().SetLegalHold(held.Key, legalHold: true, reason: "case 10"));

        Assert.True(ContentService.MoveToRecycleBin(material).Success);

        Assert.False(MediaService.GetById(held.Key)!.Trashed);
        Assert.True(MediaService.GetById(free.Key)!.Trashed);
        Assert.Contains(ComplianceLogs.AuditFor(site, held.Key), e => e.Action == AuditActions.DeleteBlocked);
    }

    [Fact]
    public async Task Trash_and_restore_move_the_elements_with_the_material_back_to_their_folder()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Restore elements");
        var folder = MediaService.CreateMedia("Elements folder " + Guid.NewGuid().ToString("N")[..6], Constants.System.Root, Constants.Conventions.MediaTypes.Folder);
        Assert.True(MediaService.Save(folder).Success);
        var element = CreateElement(folder.Id);
        var material = CreateMaterial(type, "Restored material", element);

        Assert.True(ContentService.MoveToRecycleBin(material).Success);
        Assert.True(MediaService.GetById(element.Key)!.Trashed);
        Assert.Contains(ComplianceLogs.DeletionLogFor(site, element.Key), e => e.Event == DeletionLogEvents.Trashed);

        Assert.True(ContentService.Move(ContentService.GetById(material.Key)!, Constants.System.Root).Success);
        var restored = MediaService.GetById(element.Key)!;
        Assert.False(restored.Trashed);
        Assert.Equal(folder.Id, restored.ParentId);
        Assert.Contains(ComplianceLogs.DeletionLogFor(site, element.Key), e => e.Event == DeletionLogEvents.Restored);
    }

    [Fact]
    public async Task Elements_stay_in_the_tree_when_they_do_not_follow_the_material_but_purge_still_takes_them()
    {
        var options = site.Services.GetRequiredService<IOptionsMonitor<PurgeOptions>>().CurrentValue;
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Elements stay");
        var element = CreateElement();
        var material = CreateMaterial(type, "Material, elements stay", element);

        options.ElementsFollowMaterial = false;
        try
        {
            Assert.True(ContentService.MoveToRecycleBin(material).Success);
            Assert.False(MediaService.GetById(element.Key)!.Trashed);
        }
        finally
        {
            options.ElementsFollowMaterial = true;
        }

        await PurgeAsync(0);
        Assert.Null(ContentService.GetById(material.Key));
        Assert.Null(MediaService.GetById(element.Key));
    }

    [Fact]
    public async Task Purge_needs_a_retention_manager_so_an_editor_cannot_delete_elements_through_it()
    {
        var type = await TestContent.CreateDocumentTypeWithElementsAsync(site, "Purge elements editor");
        var element = CreateElement();
        var material = CreateMaterial(type, "Editor material", element);
        Assert.True(ContentService.MoveToRecycleBin(material).Success);

        using var editor = await ManagementApi.ClientAsync(site, "editor");
        using var response = await editor.PostJsonAsync("/compliance/purge", new { restorationPeriodDays = 0, entityTypes = DocumentOnly });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(ContentService.GetById(material.Key));
        Assert.NotNull(MediaService.GetById(element.Key));
    }

    private IContent CreateMaterial(IContentType type, string name, params IMedia[] elements) =>
        TestContent.CreateDocument(site, type, $"{name} {Guid.NewGuid():N}", (TestContent.Elements, TestContent.Picked(elements)));

    private IMedia CreateElement(int parentId = Constants.System.Root)
    {
        var media = MediaService.CreateMedia("Element " + Guid.NewGuid().ToString("N")[..8], parentId, Constants.Conventions.MediaTypes.File);
        Assert.True(MediaService.Save(media).Success);
        return media;
    }

    private async Task<JsonElement> PurgeAsync(int days, bool dryRun = false)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var response = await admin.PostJsonAsync("/compliance/purge", new { restorationPeriodDays = days, dryRun, entityTypes = DocumentOnly });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static List<JsonElement> Elements(JsonElement result, Guid materialKey) =>
        [.. result.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("materialKey").GetGuid() == materialKey)];
}
