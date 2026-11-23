using System.Reflection;
using Limescape.Umbraco.Compliance.ActingUser;
using Limescape.Umbraco.Compliance.Audit;
using Limescape.Umbraco.Compliance.Persistence;
using Limescape.Umbraco.Compliance.Tests.Infrastructure;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Limescape.Umbraco.Compliance.Tests.Audit;

public sealed class AuditAndDeletionLogTests(UmbracoTestSite site)
{
    [Theory]
    [InlineData(ComplianceTables.Audit)]
    [InlineData(ComplianceTables.DeletionLog)]
    [InlineData(ComplianceTables.Approval)]
    [InlineData(ComplianceTables.RetentionClass)]
    [InlineData(ComplianceTables.RetentionAssignment)]
    [InlineData(ComplianceTables.ItemRetention)]
    public void Package_migration_creates_the_table(string table)
    {
        using var scope = site.Services.GetRequiredService<IScopeProvider>().CreateScope(autoComplete: true);

        Assert.True(scope.SqlContext.SqlSyntax.DoesTableExist(scope.Database, table), $"{table} does not exist");
    }

    [Fact]
    public async Task Audit_entry_outside_a_request_is_attributed_to_the_system_and_values_are_truncated()
    {
        var key = Guid.NewGuid();
        var writer = site.Services.GetRequiredService<IAuditWriter>();

        await writer.WriteAsync(
            new AuditRecord(AuditActions.Update, AuditEntityTypes.Document, key) { Field = "description", OldValue = "old", NewValue = new string('x', 5000) },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(ComplianceLogs.AuditFor(site, key));
        Assert.Equal(Actor.SystemId, entry.ActorId);
        Assert.Equal(ActorChannels.System, entry.Channel);
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.Equal("description", entry.Field);
        Assert.Equal("old", entry.OldValue);
        Assert.Equal(AuditValues.MaxLength, entry.NewValue!.Length);
        Assert.EndsWith(AuditValues.TruncationMarker, entry.NewValue, StringComparison.Ordinal);
        Assert.True((DateTime.UtcNow - entry.TimestampUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Trash_restore_trash_purge_gives_four_deletion_log_entries_in_order()
    {
        var key = Guid.NewGuid();
        var writer = site.Services.GetRequiredService<IDeletionLogWriter>();
        var ct = TestContext.Current.CancellationToken;

        await writer.WriteAsync(new DeletionLogRecord(DeletionLogEvents.Trashed, AuditEntityTypes.Document, key) { TitleSnapshot = "Intro video" }, ct);
        writer.Write(new DeletionLogRecord(DeletionLogEvents.Restored, AuditEntityTypes.Document, key) { TitleSnapshot = "Intro video" });
        await writer.WriteAsync(new DeletionLogRecord(DeletionLogEvents.Trashed, AuditEntityTypes.Document, key) { TitleSnapshot = "Intro video" }, ct);
        await writer.WriteAsync(
            new DeletionLogRecord(DeletionLogEvents.Purged, AuditEntityTypes.Document, key) { TitleSnapshot = "Intro video", Reason = "restorationPeriodEnded", RetentionClass = "teaching-7y" },
            ct);

        var entries = ComplianceLogs.DeletionLogFor(site, key);

        Assert.Equal(
            [DeletionLogEvents.Trashed, DeletionLogEvents.Restored, DeletionLogEvents.Trashed, DeletionLogEvents.Purged],
            entries.Select(e => e.Event));
        Assert.All(entries, e => Assert.Equal("Intro video", e.TitleSnapshot));
        Assert.All(entries, e => Assert.Equal(Actor.SystemId, e.ActorId));
        Assert.Equal("restorationPeriodEnded", entries[^1].Reason);
        Assert.Equal("teaching-7y", entries[^1].RetentionClass);
    }

    [Theory]
    [InlineData(typeof(IAuditWriter))]
    [InlineData(typeof(IDeletionLogWriter))]
    public void Writers_offer_no_update_or_delete_path(Type writerInterface)
    {
        var methods = writerInterface.GetMethods().Select(m => m.Name).Distinct().Order();

        Assert.Equal(["Write", "WriteAsync"], methods);
    }

    [Theory]
    [InlineData(typeof(AuditEntryDto))]
    [InlineData(typeof(DeletionLogEntryDto))]
    public void Log_rows_are_not_part_of_the_public_api(Type dto)
    {
        Assert.False(dto.IsPublic);
        Assert.DoesNotContain(
            typeof(IAuditWriter).Assembly.GetExportedTypes(),
            t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Any(m => m.GetParameters().Any(p => p.ParameterType == dto)));
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData(null, null)]
    public void Truncate_leaves_short_values_alone(string? value, string? expected) =>
        Assert.Equal(expected, AuditValues.Truncate(value));

    [Fact]
    public void Truncate_does_not_split_a_surrogate_pair()
    {
        var value = new string('a', AuditValues.MaxLength - 2) + "😀" + "tail";

        var truncated = AuditValues.Truncate(value)!;

        Assert.True(truncated.Length <= AuditValues.MaxLength);
        Assert.False(char.IsHighSurrogate(truncated[^2]));
    }
}
