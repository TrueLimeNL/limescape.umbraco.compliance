using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Persistence;
using Limescape.Umbraco.Compliance.Retention;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.Tests.Retention;

/// <summary>
/// Retention end to end against the real site: classes and assignments through the Management API extension as an
/// API user, recalculation by the notification handlers, shortening only with an administrator's confirmation,
/// permissions (negative tests that bypass the backoffice UI) and the daily job.
/// </summary>
public sealed class RetentionIntegrationTests(UmbracoTestSite site)
{
    private const string Api = "/compliance/retention";
    private static readonly DateTime ArchivedOn = new(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Two_classes_on_two_types_give_the_computed_end_date_of_each_archived_item()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var videoType = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var assessmentType = await TestContent.CreateDocumentTypeAsync(site, "Assessment");
        var sevenYears = await CreateClassAsync(admin, "Teaching material, 7 years after archive", years: 7, startEvent: "archived");
        var tenYears = await CreateClassAsync(admin, "Assessment material, 10 years after archive", years: 10, startEvent: "archived");
        await AssignAsync(admin, sevenYears, new { target = "contentType", contentTypeKey = videoType.Key });
        await AssignAsync(admin, tenYears, new { target = "contentType", contentTypeKey = assessmentType.Key });

        var video = TestContent.CreateDocument(site, videoType, "Intro video", (TestContent.ArchivedAt, ArchivedOn));
        var assessment = TestContent.CreateDocument(site, assessmentType, "Exam", (TestContent.ArchivedAt, ArchivedOn));

        var videoRetention = await GetItemAsync(admin, video.Key);
        var assessmentRetention = await GetItemAsync(admin, assessment.Key);
        Assert.Equal(new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc), videoRetention.GetProperty("retentionUntil").GetDateTime().ToUniversalTime());
        Assert.Equal(new DateTime(2037, 1, 15, 0, 0, 0, DateTimeKind.Utc), assessmentRetention.GetProperty("retentionUntil").GetDateTime().ToUniversalTime());
        Assert.Equal("active", videoRetention.GetProperty("status").GetString());
        Assert.Equal(sevenYears, videoRetention.GetProperty("governingClassKey").GetGuid());
        var rule = Assert.Single(videoRetention.GetProperty("rules").EnumerateArray());
        Assert.Equal("archived", rule.GetProperty("startEvent").GetString());
        Assert.Equal(TestContent.ArchivedAt, rule.GetProperty("startPropertyAlias").GetString());
        Assert.Equal("contentType", rule.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Item_that_is_not_archived_yet_has_an_open_ended_period_until_it_is_archived()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = Service.CreateClass(new RetentionClassInput("7 years after archive", new(7, 0, 0), RetentionStartEvent.Archived, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });

        var item = TestContent.CreateDocument(site, type, "Not archived yet");
        var before = Service.GetItem(item.Key)!;
        TestContent.Update(site, item.Key, (TestContent.ArchivedAt, ArchivedOn));
        var after = Service.GetItem(item.Key)!;

        Assert.Equal(RetentionStatus.Pending, before.Status);
        Assert.True(before.OpenEnded);
        Assert.Null(before.RetentionUntil);
        Assert.Equal(RetentionStatus.Active, after.Status);
        Assert.Equal(new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc), after.RetentionUntil);
    }

    [Fact]
    public async Task Last_modification_class_is_recalculated_when_the_item_is_modified()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Assessment");
        var cls = Service.CreateClass(new RetentionClassInput("10 years after last modification", new(10, 0, 0), RetentionStartEvent.LastModified, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });

        var item = TestContent.CreateDocument(site, type, "Exam");
        var first = Service.GetItem(item.Key)!.RetentionUntil!.Value;
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var modified = TestContent.Update(site, item.Key, (TestContent.Category, "changed"));
        var second = Service.GetItem(item.Key)!.RetentionUntil!.Value;

        Assert.True(second > first, $"{second:O} should be later than {first:O}");
        Assert.Equal(RetentionCalculator.AsUtc(modified.UpdateDate).AddYears(10), second, TimeSpan.FromSeconds(1));
        Assert.Contains(ComplianceLogs.AuditFor(site, item.Key), e => e.Action == AuditActions.RetentionChange && e.Field == "retentionUntil");
    }

    [Fact]
    public async Task Publication_class_is_calculated_from_the_publication()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = Service.CreateClass(new RetentionClassInput("5 years after publication", new(5, 0, 0), RetentionStartEvent.Published, RetentionEndAction.Notify));
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });

        var item = TestContent.CreateDocument(site, type, "To publish");
        Assert.True(Service.GetItem(item.Key)!.OpenEnded);
        var contentService = site.Services.GetRequiredService<IContentService>();
        Assert.True(contentService.Publish(contentService.GetById(item.Key)!, ["*"]).Success);

        var published = contentService.GetById(item.Key)!;
        var retention = Service.GetItem(item.Key)!;
        Assert.Equal(RetentionStatus.Active, retention.Status);
        Assert.Equal(RetentionCalculator.AsUtc(published.PublishDate!.Value).AddYears(5), retention.RetentionUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Longest_period_wins_and_both_classes_are_shown()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var typeClass = await CreateClassAsync(admin, "7 years after archive", years: 7, startEvent: "archived");
        var categoryClass = await CreateClassAsync(admin, "Assessment, 10 years after archive", years: 10, startEvent: "archived");
        await AssignAsync(admin, typeClass, new { target = "contentType", contentTypeKey = type.Key });
        var category = "Assessment-" + Guid.NewGuid().ToString("N")[..6];
        await AssignAsync(admin, categoryClass, new { target = "propertyValue", propertyAlias = TestContent.Category, propertyValue = category });

        var item = TestContent.CreateDocument(site, type, "Exam video", (TestContent.ArchivedAt, ArchivedOn), (TestContent.Category, category.ToLowerInvariant()));

        var retention = await GetItemAsync(admin, item.Key);
        var rules = retention.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(2, rules.Count);
        Assert.Equal(categoryClass, retention.GetProperty("governingClassKey").GetGuid());
        Assert.Equal(new DateTime(2037, 1, 15, 0, 0, 0, DateTimeKind.Utc), retention.GetProperty("retentionUntil").GetDateTime().ToUniversalTime());
        Assert.True(rules[0].GetProperty("governing").GetBoolean());
        Assert.Equal("propertyValue", rules[0].GetProperty("source").GetString());
        Assert.False(rules[1].GetProperty("governing").GetBoolean());
        Assert.Equal(new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc), rules[1].GetProperty("until").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Shorter_class_duration_keeps_the_longer_period_until_an_administrator_confirms()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        using var manager = await ManagementApi.ClientAsync(site, UmbracoTestSite.RetentionManagerGroup);
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = await CreateClassAsync(manager, "7 years after archive", years: 7, startEvent: "archived");
        await AssignAsync(manager, cls, new { target = "contentType", contentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Intro", (TestContent.ArchivedAt, ArchivedOn));
        var shorter = ClassBody("3 years after archive", years: 3, startEvent: "archived");

        using var refused = await manager.PutJsonAsync($"{Api}/class/{cls}", shorter);
        var afterRefused = Service.GetItem(item.Key)!;
        using var managerConfirm = await manager.PutJsonAsync($"{Api}/class/{cls}?confirmShortening=true", shorter);
        var afterManagerConfirm = Service.GetItem(item.Key)!;
        using var confirmed = await admin.PutJsonAsync($"{Api}/class/{cls}?confirmShortening=true", shorter);
        var afterConfirmed = Service.GetItem(item.Key)!;

        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains(item.Key, (await refused.JsonAsync()).GetProperty("recalculation").GetProperty("shorteningRefused").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc), afterRefused.RetentionUntil);
        Assert.True(afterRefused.HeldFromEarlier);

        Assert.Equal(HttpStatusCode.Forbidden, managerConfirm.StatusCode);
        Assert.Equal(RetentionErrorCodes.ShorteningRequiresAdministrator, (await managerConfirm.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(new DateTime(2034, 1, 15, 0, 0, 0, DateTimeKind.Utc), afterManagerConfirm.RetentionUntil);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc), afterConfirmed.RetentionUntil);
        Assert.False(afterConfirmed.HeldFromEarlier);
    }

    [Fact]
    public async Task Earlier_date_property_on_save_does_not_shorten_the_stored_period()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Contract");
        var cls = Service.CreateClass(new RetentionClassInput("2 years after contract end", new(2, 0, 0), RetentionStartEvent.DateProperty, RetentionEndAction.Notify)
        {
            StartPropertyAlias = TestContent.ContractEnd,
        });
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Contract", (TestContent.ContractEnd, new DateTime(2030, 6, 1)));

        TestContent.Update(site, item.Key, (TestContent.ContractEnd, new DateTime(2028, 6, 1)));

        var retention = Service.GetItem(item.Key)!;
        Assert.Equal(new DateTime(2032, 6, 1, 0, 0, 0, DateTimeKind.Utc), retention.RetentionUntil);
        Assert.True(retention.HeldFromEarlier);
    }

    [Fact]
    public async Task Item_assignment_overrides_the_type_assignment()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var typeClass = await CreateClassAsync(admin, "7 years after archive", years: 7, startEvent: "archived");
        var itemClass = await CreateClassAsync(admin, "20 years after archive", years: 20, startEvent: "archived");
        await AssignAsync(admin, typeClass, new { target = "contentType", contentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Special", (TestContent.ArchivedAt, ArchivedOn));

        using var response = await admin.PostJsonAsync($"{Api}/assignment", new { classKey = itemClass, target = "item", itemKey = item.Key });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var retention = Service.GetItem(item.Key)!;
        Assert.Equal(itemClass, Assert.Single(retention.Rules).ClassKey);
        Assert.Equal(new DateTime(2047, 1, 15, 0, 0, 0, DateTimeKind.Utc), retention.RetentionUntil);
    }

    [Fact]
    public async Task Users_without_manage_permission_cannot_change_retention_through_the_api()
    {
        using var editor = await ManagementApi.ClientAsync(site, "editor");
        using var anonymous = site.CreateClient();
        anonymous.BaseAddress = new Uri("https://localhost/");
        var body = ClassBody("Should not exist", years: 1, startEvent: "lastModified");
        var existing = Service.CreateClass(new RetentionClassInput("Existing", new(1, 0, 0), RetentionStartEvent.LastModified, RetentionEndAction.Notify));

        using var create = await editor.PostJsonAsync($"{Api}/class", body);
        using var update = await editor.PutJsonAsync($"{Api}/class/{existing.Key}", body);
        using var delete = await editor.DeletePathAsync($"{Api}/class/{existing.Key}");
        using var assign = await editor.PostJsonAsync($"{Api}/assignment", new { classKey = existing.Key, target = "item", itemKey = Guid.NewGuid() });
        using var ended = await editor.GetPathAsync($"{Api}/ended");
        using var anonymousCreate = await anonymous.PostJsonAsync($"{Api}/class", body);
        using var read = await editor.GetPathAsync($"{Api}/class/{existing.Key}");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ended.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain(Service.GetClasses(), c => c.Name == "Should not exist");
        Assert.Equal("Existing", Service.GetClass(existing.Key)!.Name);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"durationYears\":1,\"startEvent\":\"whenever\",\"endAction\":\"notify\"}")]
    [InlineData("{\"name\":\"x\",\"durationYears\":1,\"startEvent\":\"dateProperty\",\"endAction\":\"notify\"}")]
    [InlineData("{\"name\":\"\",\"durationYears\":1,\"startEvent\":\"archived\",\"endAction\":\"notify\"}")]
    [InlineData("{\"name\":\"x\",\"startEvent\":\"archived\",\"endAction\":\"notify\"}")]
    [InlineData("{\"name\":\"x\",\"durationYears\":-1,\"startEvent\":\"archived\",\"endAction\":\"notify\"}")]
    [InlineData("{\"name\":\"x\",\"durationYears\":1,\"startEvent\":\"archived\",\"endAction\":\"delete\"}")]
    public async Task Invalid_class_is_refused_with_a_stable_code(string json)
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var response = await admin.PostAsync(
            new Uri(ManagementApi.Base + Api + "/class", UriKind.Relative),
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RetentionErrorCodes.Invalid, (await response.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Assigned_class_cannot_be_deleted()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Video");
        var cls = await CreateClassAsync(admin, "In use", years: 1, startEvent: "lastModified");
        await AssignAsync(admin, cls, new { target = "contentType", contentTypeKey = type.Key });

        using var response = await admin.DeletePathAsync($"{Api}/class/{cls}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RetentionErrorCodes.ClassInUse, (await response.JsonAsync()).GetProperty("code").GetString());
        Assert.NotNull(Service.GetClass(cls));
    }

    [Fact]
    public async Task Class_changes_are_audited_with_the_acting_api_user()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        var cls = await CreateClassAsync(admin, "Audited class", years: 2, startEvent: "published");

        var entry = Assert.Single(ComplianceLogs.AuditFor(site, cls));
        Assert.Equal(AuditActions.RetentionChange, entry.Action);
        Assert.Equal(RetentionAuditEntityTypes.RetentionClass, entry.EntityType);
        Assert.Equal("api", entry.Channel);
        Assert.NotNull(entry.TechnicalUserKey);
        Assert.Contains("Audited class", entry.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daily_job_reports_ended_retention_once_and_deletes_nothing()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Contract");
        var cls = Service.CreateClass(new RetentionClassInput("1 day after contract end", new(0, 0, 1), RetentionStartEvent.DateProperty, RetentionEndAction.AllowDeletion)
        {
            StartPropertyAlias = TestContent.ContractEnd,
        });
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var ended = TestContent.CreateDocument(site, type, "Old contract", (TestContent.ContractEnd, new DateTime(2020, 1, 1)));
        var running = TestContent.CreateDocument(site, type, "Running contract", (TestContent.ContractEnd, new DateTime(2099, 1, 1)));
        var job = site.Services.GetServices<IRecurringBackgroundJob>().OfType<RetentionJob>().Single();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(RetentionStatus.Ended, Service.GetItem(ended.Key)!.Status);
        await job.RunAsync(ct);
        await job.RunAsync(ct);

        var reports = RetentionEndedRecorder.Received.SelectMany(n => n.Items).Where(i => i.ItemKey == ended.Key).ToList();
        var report = Assert.Single(reports);
        Assert.Equal(RetentionEndAction.AllowDeletion, report.EndAction);
        Assert.DoesNotContain(RetentionEndedRecorder.Received.SelectMany(n => n.Items), i => i.ItemKey == running.Key);
        var stillThere = site.Services.GetRequiredService<IContentService>().GetById(ended.Key);
        Assert.NotNull(stillThere);
        Assert.False(stillThere.Trashed);
        Assert.NotNull(Service.GetItem(ended.Key)!.EndReportedUtc);
        Assert.Contains(ComplianceLogs.AuditFor(site, ended.Key), e => e.Field == "retentionStatus" && e.NewValue == "ended; deletion allowed");
        Assert.Contains(Service.GetEnded(DateTime.UtcNow, 0, 10_000), i => i.ItemKey == ended.Key);
    }

    [Fact]
    public async Task Ended_list_is_available_to_managers_through_the_api()
    {
        using var manager = await ManagementApi.ClientAsync(site, UmbracoTestSite.RetentionManagerGroup);
        var type = await TestContent.CreateDocumentTypeAsync(site, "Contract");
        var cls = Service.CreateClass(new RetentionClassInput("Ended quickly", new(0, 0, 1), RetentionStartEvent.DateProperty, RetentionEndAction.Notify)
        {
            StartPropertyAlias = TestContent.ContractEnd,
        });
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "Ended", (TestContent.ContractEnd, new DateTime(2021, 3, 1)));

        using var response = await manager.GetPathAsync($"{Api}/ended?take=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.JsonAsync()).GetProperty("items").EnumerateArray().ToList();
        var listed = Assert.Single(items, i => i.GetProperty("itemKey").GetGuid() == item.Key);
        Assert.Equal("ended", listed.GetProperty("status").GetString());
        Assert.Equal("notify", listed.GetProperty("endAction").GetString());
    }

    [Fact]
    public async Task Permanently_deleted_item_loses_its_stored_retention()
    {
        var type = await TestContent.CreateDocumentTypeAsync(site, "Contract");
        var cls = Service.CreateClass(new RetentionClassInput("Ended", new(0, 0, 1), RetentionStartEvent.DateProperty, RetentionEndAction.AllowDeletion)
        {
            StartPropertyAlias = TestContent.ContractEnd,
        });
        Service.CreateAssignment(new RetentionAssignment(0, cls.Key, RetentionAssignmentTarget.ContentType) { ContentTypeKey = type.Key });
        var item = TestContent.CreateDocument(site, type, "To delete", (TestContent.ContractEnd, new DateTime(2020, 1, 1)));
        var contentService = site.Services.GetRequiredService<IContentService>();

        contentService.Delete(contentService.GetById(item.Key)!);

        Assert.Null(Service.GetItem(item.Key));
        using var scope = site.Services.GetRequiredService<IScopeProvider>().CreateScope(autoComplete: true);
        Assert.Equal(0, scope.Database.ExecuteScalar<int>(scope.SqlContext.Sql().SelectCount().From<ItemRetentionDto>().Where<ItemRetentionDto>(r => r.ItemKey == item.Key)));
    }

    [Fact]
    public async Task Item_endpoint_returns_404_for_an_unknown_item_and_none_for_an_item_without_class()
    {
        using var editor = await ManagementApi.ClientAsync(site, "editor");
        var type = await TestContent.CreateDocumentTypeAsync(site, "Unassigned");
        var item = TestContent.CreateDocument(site, type, "No class");

        using var unknown = await editor.GetPathAsync($"{Api}/item/{Guid.NewGuid()}");
        using var none = await editor.GetPathAsync($"{Api}/item/{item.Key}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        var body = await none.JsonAsync();
        Assert.Equal("none", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("rules").EnumerateArray());
    }

    private IRetentionService Service => site.Services.GetRequiredService<IRetentionService>();

    private static object ClassBody(string name, int years, string startEvent, string endAction = "notify") =>
        new { name, durationYears = years, startEvent, endAction };

    private static async Task<Guid> CreateClassAsync(HttpClient client, string name, int years, string startEvent)
    {
        using var response = await client.PostJsonAsync($"{Api}/class", ClassBody(name, years, startEvent));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("key").GetGuid();
    }

    private static async Task AssignAsync(HttpClient client, Guid classKey, object target)
    {
        var json = JsonSerializer.SerializeToNode(target)!.AsObject();
        json["classKey"] = classKey;
        using var response = await client.PostJsonAsync($"{Api}/assignment", json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {body}");
    }

    private static async Task<JsonElement> GetItemAsync(HttpClient client, Guid key)
    {
        using var response = await client.GetPathAsync($"{Api}/item/{key}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
