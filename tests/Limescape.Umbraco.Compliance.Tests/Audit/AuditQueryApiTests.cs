using System.Net;
using System.Text.Json;
using Limescape.Umbraco.Compliance.Api;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Limescape.Umbraco.Compliance.Tests.Audit;

/// <summary>
/// The audit and deletion-log query API with filters (person, item, action, date) and paging.
/// </summary>
public sealed class AuditQueryApiTests(UmbracoTestSite site)
{
    [Fact]
    public async Task The_audit_log_is_filtered_by_item_and_action_and_paged_newest_first()
    {
        var key = Guid.NewGuid();
        var writer = site.Services.GetRequiredService<IAuditWriter>();
        for (var i = 1; i <= 5; i++)
        {
            writer.Write(new AuditRecord(AuditActions.Update, AuditEntityTypes.Document, key) { Field = "title", NewValue = $"v{i}" });
        }

        writer.Write(new AuditRecord(AuditActions.Publish, AuditEntityTypes.Document, key));
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        var page = await GetAsync(admin, $"/compliance/audit?item={key}&action=update&skip=1&take=2");

        Assert.Equal(5, page.GetProperty("total").GetInt64());
        Assert.Equal((1, 2), (page.GetProperty("skip").GetInt32(), page.GetProperty("take").GetInt32()));
        Assert.Equal(["v4", "v3"], page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("newValue").GetString()));
    }

    [Fact]
    public async Task The_audit_log_is_filtered_by_person_and_date_range()
    {
        var key = Guid.NewGuid();
        var person = "query-person-" + Guid.NewGuid().ToString("N")[..6];
        using (var client = site.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Limescape-Acting-User", TestIssuer.Shared.CreateToken(subject: person));
            using var probe = await client.PostAsync(new Uri($"{UmbracoTestSite.AuditProbePath}?key={key}", UriKind.Relative), null, TestContext.Current.CancellationToken);
            Assert.True(probe.IsSuccessStatusCode);
        }

        using var admin = await ManagementApi.ClientAsync(site, "admin");
        var byPerson = await GetAsync(admin, $"/compliance/audit?person={person}");
        var entry = Assert.Single(byPerson.GetProperty("items").EnumerateArray());
        Assert.Equal(key, entry.GetProperty("entityKey").GetGuid());

        var today = DateTime.UtcNow.Date;
        Assert.Equal(1, (await GetAsync(admin, $"/compliance/audit?person={person}&from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")).GetProperty("total").GetInt64());
        Assert.Equal(0, (await GetAsync(admin, $"/compliance/audit?person={person}&from={today.AddDays(1):yyyy-MM-dd}")).GetProperty("total").GetInt64());
        Assert.Equal(0, (await GetAsync(admin, $"/compliance/audit?person={person}&to={today:yyyy-MM-dd}")).GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task The_deletion_log_is_filtered_by_item_and_event()
    {
        var key = Guid.NewGuid();
        var writer = site.Services.GetRequiredService<IDeletionLogWriter>();
        writer.Write(new DeletionLogRecord(DeletionLogEvents.Trashed, AuditEntityTypes.Media, key) { TitleSnapshot = "Scan" });
        writer.Write(new DeletionLogRecord(DeletionLogEvents.Purged, AuditEntityTypes.Media, key) { Reason = PurgeReasons.RestorationPeriodEnded, RetentionClass = "Scans" });
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        var all = await GetAsync(admin, $"/compliance/deletion-log?item={key}");
        var purged = await GetAsync(admin, $"/compliance/deletion-log?item={key}&action=purged");

        Assert.Equal(2, all.GetProperty("total").GetInt64());
        var entry = Assert.Single(purged.GetProperty("items").EnumerateArray());
        Assert.Equal(("purged", "Scans", PurgeReasons.RestorationPeriodEnded), (entry.GetProperty("event").GetString(), entry.GetProperty("retentionClass").GetString(), entry.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task An_editor_can_read_the_history_of_an_item_but_not_the_whole_log()
    {
        var key = Guid.NewGuid();
        site.Services.GetRequiredService<IAuditWriter>().Write(new AuditRecord(AuditActions.Update, AuditEntityTypes.Document, key));
        using var editor = await ManagementApi.ClientAsync(site, "editor");

        using var whole = await editor.GetPathAsync("/compliance/audit");
        Assert.Equal(HttpStatusCode.Forbidden, whole.StatusCode);
        Assert.Equal(AuditController.RequiresManagerCode, (await whole.JsonAsync()).GetProperty("code").GetString());

        using var deletionLog = await editor.GetPathAsync("/compliance/deletion-log");
        Assert.Equal(HttpStatusCode.Forbidden, deletionLog.StatusCode);

        Assert.Equal(1, (await GetAsync(editor, $"/compliance/audit?item={key}")).GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Invalid_filters_are_refused()
    {
        using var admin = await ManagementApi.ClientAsync(site, "admin");

        using var badDate = await admin.GetPathAsync("/compliance/audit?from=yesterday");
        using var badTake = await admin.GetPathAsync("/compliance/audit?take=501");
        using var anonymous = await site.CreateClient().GetAsync(new Uri(ManagementApi.Base + "/compliance/audit", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
        Assert.Equal(AuditController.InvalidFilterCode, (await badDate.JsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badTake.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public void The_log_retention_job_removes_entries_older_than_the_configured_years()
    {
        var key = Guid.NewGuid();
        var old = new FakeTime(DateTimeOffset.UtcNow.AddYears(-8));
        var actingUser = site.Services.GetRequiredService<Limescape.Umbraco.Compliance.ActingUser.IActingUserAccessor>();
        var scopes = site.Services.GetRequiredService<global::Umbraco.Cms.Infrastructure.Scoping.IScopeProvider>();
        new AuditWriter(scopes, actingUser, old).Write(new AuditRecord(AuditActions.Update, AuditEntityTypes.Document, key));
        new DeletionLogWriter(scopes, actingUser, old).Write(new DeletionLogRecord(DeletionLogEvents.Trashed, AuditEntityTypes.Document, key));
        site.Services.GetRequiredService<IAuditWriter>().Write(new AuditRecord(AuditActions.Publish, AuditEntityTypes.Document, key));

        var job = new LogRetentionJob(scopes, Options.Create(new AuditOptions()), TimeProvider.System, NullLogger<LogRetentionJob>.Instance);
        var (audit, deletionLog) = job.Run();

        Assert.True(audit >= 1 && deletionLog >= 1);
        Assert.Equal(AuditActions.Publish, Assert.Single(ComplianceLogs.AuditFor(site, key)).Action);
        Assert.Empty(ComplianceLogs.DeletionLogFor(site, key));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
