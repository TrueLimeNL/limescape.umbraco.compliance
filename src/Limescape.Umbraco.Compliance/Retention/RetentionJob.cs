using Limescape.Umbraco.Compliance.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Daily job: finds items whose retention period ended and that were not reported yet, raises one
/// <see cref="RetentionEndedNotification"/> per batch, writes an audit entry per item and marks them reported.
/// It never deletes. Runs on the scheduling server only.
/// </summary>
public sealed partial class RetentionJob(
    IRetentionService retentionService,
    IEventAggregator eventAggregator,
    IAuditWriter auditWriter,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionJob> logger) : IRecurringBackgroundJob
{
    private const int BatchSize = 200;

    /// <inheritdoc />
    public TimeSpan Period => options.Value.JobPeriod;

    /// <inheritdoc />
    public TimeSpan Delay => options.Value.JobDelay;

    /// <inheritdoc />
    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    /// <inheritdoc />
    public event EventHandler PeriodChanged { add { } remove { } }

    /// <inheritdoc />
    public Task RunJobAsync() => RunAsync(CancellationToken.None);

    /// <summary>Runs one pass. Returns the number of items reported.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var reported = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Always the first page: reported items drop out of the unreported selection.
            var ended = retentionService.GetEnded(now, 0, BatchSize, onlyUnreported: true);
            if (ended.Count == 0)
            {
                break;
            }

            await eventAggregator.PublishAsync(new RetentionEndedNotification(ended), cancellationToken).ConfigureAwait(false);
            foreach (var item in ended)
            {
                await auditWriter.WriteAsync(
                    new AuditRecord(AuditActions.RetentionChange, item.EntityType, item.ItemKey)
                    {
                        Field = "retentionStatus",
                        OldValue = "active",
                        NewValue = item.EndAction == RetentionEndAction.AllowDeletion ? "ended; deletion allowed" : "ended; review",
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            retentionService.MarkEndReported(ended.Select(i => i.ItemKey), now);
            reported += ended.Count;
        }

        if (reported > 0)
        {
            LogReported(logger, reported);
        }

        return reported;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention ended for {Count} item(s); reported for review or deletion")]
    private static partial void LogReported(ILogger logger, int count);
}
