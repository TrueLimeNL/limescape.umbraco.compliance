using Limescape.Umbraco.Compliance.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Daily job that removes audit and deletion-log entries older than <see cref="AuditOptions.LogRetentionYears"/>
/// (default 7 years). This is the only code path that removes log entries; the writers have no update or delete.
/// Runs on the scheduling server only.
/// </summary>
public sealed partial class LogRetentionJob(
    IScopeProvider scopeProvider,
    IOptions<AuditOptions> options,
    TimeProvider timeProvider,
    ILogger<LogRetentionJob> logger) : IRecurringBackgroundJob
{
    /// <inheritdoc />
    public TimeSpan Period => options.Value.LogRetentionJobPeriod;

    /// <inheritdoc />
    public TimeSpan Delay => options.Value.LogRetentionJobDelay;

    /// <inheritdoc />
    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    /// <inheritdoc />
    public event EventHandler PeriodChanged { add { } remove { } }

    /// <inheritdoc />
    public Task RunJobAsync()
    {
        Run();
        return Task.CompletedTask;
    }

    /// <summary>Runs one pass. Returns the number of audit and deletion-log entries removed.</summary>
    public (int Audit, int DeletionLog) Run()
    {
        var years = options.Value.LogRetentionYears;
        if (years <= 0)
        {
            return (0, 0);
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddYears(-years);
        using var scope = scopeProvider.CreateScope();
        var audit = scope.Database.Execute(scope.SqlContext.Sql().Delete<AuditEntryDto>().Where<AuditEntryDto>(x => x.TimestampUtc < cutoff));
        var deletionLog = scope.Database.Execute(scope.SqlContext.Sql().Delete<DeletionLogEntryDto>().Where<DeletionLogEntryDto>(x => x.TimestampUtc < cutoff));
        scope.Complete();

        if (audit + deletionLog > 0)
        {
            LogRemoved(logger, audit, deletionLog, years);
        }

        return (audit, deletionLog);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Log retention: removed {Audit} audit and {DeletionLog} deletion-log entries older than {Years} years")]
    private static partial void LogRemoved(ILogger logger, int audit, int deletionLog, int years);
}
