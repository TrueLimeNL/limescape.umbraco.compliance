using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.DeletionBlock;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Limescape.Umbraco.Compliance.Tests.DeletionBlock;

/// <summary>
/// Route matrix: every route by which a document or media item can leave the tree
/// is tried on an item under retention or legal hold, against the real site. The Management API rows are the routes the
/// backoffice uses (it calls the same endpoints); the service rows are what a scheduled job or an integration inside the
/// site calls, without any UI or HTTP context. Each refusal must leave the item in place.
/// </summary>
public sealed class DeletionBlockRouteMatrixTests(UmbracoTestSite site)
{
    private const string Code = DeletionBlockErrorCodes.RetentionBlocksDeletion;

    public static TheoryData<string> Kinds => ["document", "media"];

    // ---- Management API (backoffice and integrations) ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Trash_through_the_management_api_is_refused_with_code_message_and_audit_entry(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var item = await CreateRetainedAsync(kind, "Retained");

        using var response = await admin.PutAsync(new Uri($"{ManagementApi.Base}/{kind}/{item.Key}/move-to-recycle-bin", UriKind.Relative), null, TestContext.Current.CancellationToken);

        await AssertBlockedAsync(response, item.Key);
        Assert.True(response.Headers.TryGetValues("Umb-Notifications", out var notifications), "the backoffice shows the Umb-Notifications header");
        Assert.Contains("Deletion not allowed: the retention period runs until", string.Concat(notifications));
        Assert.False(Load(item.Key)!.Trashed);
        var audit = Assert.Single(ComplianceLogs.AuditFor(site, item.Key), e => e.Action == AuditActions.DeleteBlocked);
        Assert.Equal("trash", audit.Field);
        Assert.Contains("retention until", audit.NewValue, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Delete_through_the_management_api_is_refused(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var item = await CreateRetainedAsync(kind, "Retained");

        using var response = await admin.DeletePathAsync($"/{kind}/{item.Key}");

        await AssertBlockedAsync(response, item.Key);
        Assert.NotNull(Load(item.Key));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Delete_from_the_recycle_bin_is_refused_for_an_item_put_on_legal_hold_after_it_was_trashed(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var item = await CreateAsync(kind, "Trashed, then held");
        using (var trashed = await admin.PutAsync(new Uri($"{ManagementApi.Base}/{kind}/{item.Key}/move-to-recycle-bin", UriKind.Relative), null, TestContext.Current.CancellationToken))
        {
            Assert.True(trashed.IsSuccessStatusCode, await trashed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await SetLegalHoldAsync(admin, item.Key, true);
        using var response = await admin.DeletePathAsync($"/recycle-bin/{kind}/{item.Key}");

        var problem = await AssertBlockedAsync(response, item.Key);
        Assert.Equal("legalHold", problem.GetProperty("blockedItems")[0].GetProperty("reason").GetString());
        Assert.True(Load(item.Key)!.Trashed);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Emptying_the_recycle_bin_deletes_the_free_items_and_keeps_the_held_ones(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var free = await CreateAsync(kind, "Free");
        var held = await CreateAsync(kind, "Held");
        Trash(free);
        Trash(held);
        Assert.NotNull(Blocks.SetLegalHold(held.Key, legalHold: true, reason: null));

        using var response = await admin.DeletePathAsync($"/recycle-bin/{kind}");

        var problem = await AssertBlockedAsync(response, held.Key);
        Assert.Contains("emptied partially", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null(Load(free.Key));
        Assert.True(Load(held.Key)!.Trashed);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Bulk_trash_moves_the_free_items_and_refuses_the_retained_ones_per_item(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var free = new[] { await CreateAsync(kind, "Free 1"), await CreateAsync(kind, "Free 2"), await CreateAsync(kind, "Free 3") };
        var retained = new[] { await CreateRetainedAsync(kind, "Retained 1"), await CreateRetainedAsync(kind, "Retained 2") };

        // The backoffice bulk action calls the endpoint once per selected item.
        var results = new Dictionary<Guid, HttpStatusCode>();
        foreach (var item in free.Concat(retained))
        {
            using var response = await admin.PutAsync(new Uri($"{ManagementApi.Base}/{kind}/{item.Key}/move-to-recycle-bin", UriKind.Relative), null, TestContext.Current.CancellationToken);
            results[item.Key] = response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                Assert.Equal(Code, (await response.JsonAsync()).GetProperty("code").GetString());
            }
        }

        Assert.All(free, i => Assert.True(Load(i.Key)!.Trashed));
        Assert.All(retained, i => Assert.False(Load(i.Key)!.Trashed));
        Assert.Equal(3, results.Values.Count(s => s == HttpStatusCode.OK));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Trashing_a_parent_is_refused_when_a_descendant_is_retained(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var parent = await CreateAsync(kind, "Folder");
        var child = await CreateRetainedAsync(kind, "Retained child", parent.Id);

        using var response = await admin.PutAsync(new Uri($"{ManagementApi.Base}/{kind}/{parent.Key}/move-to-recycle-bin", UriKind.Relative), null, TestContext.Current.CancellationToken);

        var problem = await AssertBlockedAsync(response, child.Key);
        Assert.Equal(parent.Key, problem.GetProperty("blockedItems")[0].GetProperty("viaItemKey").GetGuid());
        Assert.Contains("Retained child", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(Load(parent.Key)!.Trashed);
        Assert.False(Load(child.Key)!.Trashed);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Deleting_the_type_of_a_retained_item_is_refused(string kind)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await CreateTypeAsync(kind);
        var item = await CreateRetainedAsync(kind, "Retained", type: type);

        using var response = await admin.DeletePathAsync($"/{kind}-type/{type.Key}");

        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(Load(item.Key));
        Assert.NotNull(kind == "document" ? site.Services.GetRequiredService<IContentTypeService>().Get(type.Key) : site.Services.GetRequiredService<IMediaTypeService>().Get(type.Key));
    }

    [Fact]
    public async Task Message_is_dutch_for_an_api_caller_that_asks_for_dutch()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = Service.CreateClass(new RetentionClassInput("Onderwijsmateriaal", new(7, 0, 0), RetentionStartEvent.Archived, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Les 1", (TestContent.ArchivedAt, new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{ManagementApi.Base}/document/{item.Key}/move-to-recycle-bin", UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd("nl-NL");
        using var response = await admin.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await AssertBlockedAsync(response, item.Key);
        Assert.Equal("Verwijderen niet toegestaan: bewaartermijn loopt tot 15 januari 2034 (Onderwijsmateriaal).", problem.GetProperty("detail").GetString());
        Assert.Equal("Verwijderen geblokkeerd", problem.GetProperty("title").GetString());
    }

    // ---- Services (scheduled jobs, integrations inside the site): no HTTP context, no UI ----

    [Fact]
    public async Task Every_content_service_route_is_refused_without_any_http_context()
    {
        var contentService = site.Services.GetRequiredService<IContentService>();
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var retained = await CreateRetainedAsync("document", "Retained", type: type);
        var held = await CreateAsync("document", "Held in bin");
        Trash(held);
        Blocks.SetLegalHold(held.Key, legalHold: true, reason: null);

        // Runs on a thread-pool thread without an HttpContext, like a recurring background job.
        await Task.Run(() =>
        {
            Assert.False(contentService.MoveToRecycleBin(contentService.GetById(retained.Key)!).Success);
            Assert.False(contentService.Delete(contentService.GetById(retained.Key)!).Success);
            Assert.False(contentService.Delete(contentService.GetById(held.Key)!).Success);
            Assert.False(contentService.EmptyRecycleBin().Success);
            contentService.DeleteOfTypes([type.Id]);
        }, TestContext.Current.CancellationToken);

        Assert.False(contentService.GetById(retained.Key)!.Trashed);
        Assert.NotNull(contentService.GetById(held.Key));
    }

    [Fact]
    public async Task Every_media_service_route_is_refused_without_any_http_context()
    {
        var mediaService = site.Services.GetRequiredService<IMediaService>();
        var retained = await CreateRetainedAsync("media", "Retained");
        var held = await CreateAsync("media", "Held in bin");
        Trash(held);
        Blocks.SetLegalHold(held.Key, legalHold: true, reason: null);

        await Task.Run(() =>
        {
            mediaService.MoveToRecycleBin(mediaService.GetById(retained.Key)!);
            Assert.False(mediaService.Delete(mediaService.GetById(retained.Key)!).Success);
            Assert.False(mediaService.Delete(mediaService.GetById(held.Key)!).Success);
            Assert.False(mediaService.EmptyRecycleBin().Success);
        }, TestContext.Current.CancellationToken);

        Assert.False(mediaService.GetById(retained.Key)!.Trashed);
        Assert.NotNull(mediaService.GetById(held.Key));
    }

    // ---- What blocks and what does not ----

    [Fact]
    public async Task Item_that_is_not_archived_yet_is_blocked_because_its_period_is_open_ended()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = Service.CreateClass(new RetentionClassInput("7 years after archive", new(7, 0, 0), RetentionStartEvent.Archived, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Not archived");

        using var response = await admin.DeletePathAsync($"/document/{item.Key}");

        var problem = await AssertBlockedAsync(response, item.Key);
        Assert.Equal("retentionOpenEnded", problem.GetProperty("blockedItems")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Legal_hold_blocks_after_retention_ended_until_it_is_lifted()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Contract");
        var cls = Service.CreateClass(new RetentionClassInput("1 year after contract end", new(1, 0, 0), RetentionStartEvent.DateProperty, RetentionEndAction.AllowDeletion) { StartPropertyAlias = TestContent.ContractEnd });
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Old contract", (TestContent.ContractEnd, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(RetentionStatus.Ended, Service.GetItem(item.Key)!.Status);

        await SetLegalHoldAsync(admin, item.Key, true);
        using (var refused = await admin.DeletePathAsync($"/document/{item.Key}"))
        {
            await AssertBlockedAsync(refused, item.Key);
        }

        await SetLegalHoldAsync(admin, item.Key, false);
        using var allowed = await admin.DeletePathAsync($"/document/{item.Key}");

        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(Load(item.Key));
        Assert.Null(Service.GetItem(item.Key));
    }

    [Fact]
    public async Task Item_without_retention_or_hold_is_deleted_normally()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var item = await CreateAsync("document", "Free");

        using var response = await admin.DeletePathAsync($"/document/{item.Key}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(Load(item.Key));
        Assert.DoesNotContain(ComplianceLogs.AuditFor(site, item.Key), e => e.Action == AuditActions.DeleteBlocked);
    }

    // ---- Legal hold flag: administrators only, audited ----

    [Theory]
    [InlineData("editor")]
    [InlineData(UmbracoTestSite.RetentionManagerGroup)]
    public async Task Only_an_administrator_can_set_a_legal_hold(string group)
    {
        using var client = await ManagementApi.ClientAsync(site, group);
        var item = await CreateAsync("document", "Item");

        using var response = await client.PutJsonAsync($"/compliance/retention/item/{item.Key}/legal-hold", new { legalHold = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DeletionBlockErrorCodes.LegalHoldRequiresAdministrator, (await response.JsonAsync()).GetProperty("code").GetString());
        Assert.False(Service.GetItem(item.Key)!.LegalHold);
    }

    [Fact]
    public async Task Anonymous_caller_cannot_set_a_legal_hold()
    {
        using var anonymous = site.CreateClient();
        anonymous.BaseAddress = new Uri("https://localhost/");
        var item = await CreateAsync("document", "Item");

        using var response = await anonymous.PutJsonAsync($"/compliance/retention/item/{item.Key}/legal-hold", new { legalHold = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(Service.GetItem(item.Key)!.LegalHold);
    }

    [Fact]
    public async Task Administrator_sets_and_lifts_a_legal_hold_and_both_are_audited()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var item = await CreateAsync("document", "Item");

        var set = await SetLegalHoldAsync(admin, item.Key, true, "Case 2026-17");
        var lifted = await SetLegalHoldAsync(admin, item.Key, false);

        Assert.True(set.GetProperty("legalHold").GetBoolean());
        Assert.False(lifted.GetProperty("legalHold").GetBoolean());
        var entries = ComplianceLogs.AuditFor(site, item.Key).Where(e => e.Field == "legalHold").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("true; Case 2026-17", entries[0].NewValue);
        Assert.Equal("false", entries[1].NewValue);
        Assert.All(entries, e => Assert.Equal("Integration (admin)", e.ActorName));
    }

    [Fact]
    public async Task Legal_hold_on_an_unknown_item_gives_404()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PutJsonAsync($"/compliance/retention/item/{Guid.NewGuid()}/legal-hold", new { legalHold = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers ----

    private IRetentionService Service => site.Services.GetRequiredService<IRetentionService>();

    private IDeletionBlockService Blocks => site.Services.GetRequiredService<IDeletionBlockService>();

    private static async Task<JsonElement> AssertBlockedAsync(HttpResponseMessage response, Guid blockedKey)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(response.IsSuccessStatusCode, body);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(Code, problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("blockedItems").EnumerateArray(), b => b.GetProperty("itemKey").GetGuid() == blockedKey);
        return problem;
    }

    private static async Task<JsonElement> SetLegalHoldAsync(HttpClient admin, Guid key, bool legalHold, string? reason = null)
    {
        using var response = await admin.PutJsonAsync($"/compliance/retention/item/{key}/legal-hold", new { legalHold, reason });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>A document or media item under a 10-year retention from its last modification (active at once).</summary>
    private async Task<IContentBase> CreateRetainedAsync(string kind, string name, int parentId = Constants.System.Root, IContentTypeComposition? type = null)
    {
        var item = await CreateAsync(kind, name, parentId, type);
        var cls = Service.CreateClass(new RetentionClassInput("Teaching material " + Guid.NewGuid().ToString("N")[..6], new(10, 0, 0), RetentionStartEvent.LastModified, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.Item) { ItemKey = item.Key });
        Assert.Equal(RetentionStatus.Active, Service.GetItem(item.Key)!.Status);
        return item;
    }

    private async Task<IContentBase> CreateAsync(string kind, string name, int parentId = Constants.System.Root, IContentTypeComposition? type = null)
    {
        if (kind == "document")
        {
            var contentType = (IContentType?)type ?? await TestContent.CreateDocumentTypeAsync(site, "Page");
            var contentService = site.Services.GetRequiredService<IContentService>();
            var content = contentService.Create(name, parentId, contentType);
            Assert.True(contentService.Save(content).Success);
            return content;
        }

        var mediaService = site.Services.GetRequiredService<IMediaService>();
        var media = mediaService.CreateMedia(name, parentId, type?.Alias ?? Constants.Conventions.MediaTypes.Folder);
        Assert.True(mediaService.Save(media).Success);
        return media;
    }

    private async Task<IContentTypeComposition> CreateTypeAsync(string kind)
    {
        if (kind == "document")
        {
            return await TestContent.CreateDocumentTypeAsync(site, "Deletable type");
        }

        var mediaType = new MediaType(site.Services.GetRequiredService<IShortStringHelper>(), -1)
        {
            Alias = "m" + Guid.NewGuid().ToString("N")[..12],
            Name = "Deletable media type",
            AllowedAsRoot = true,
        };
        var created = await site.Services.GetRequiredService<IMediaTypeService>().CreateAsync(mediaType, Constants.Security.SuperUserKey);
        Assert.True(created.Success, $"creating media type failed: {created.Result}");
        return mediaType;
    }

    private void Trash(IContentBase item)
    {
        if (item is IContent content)
        {
            Assert.True(site.Services.GetRequiredService<IContentService>().MoveToRecycleBin(content).Success);
        }
        else
        {
            Assert.True(site.Services.GetRequiredService<IMediaService>().MoveToRecycleBin((IMedia)item).Success);
        }
    }

    private IContentBase? Load(Guid key) =>
        (IContentBase?)site.Services.GetRequiredService<IContentService>().GetById(key) ?? site.Services.GetRequiredService<IMediaService>().GetById(key);
}
