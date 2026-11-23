namespace Limescape.Umbraco.Compliance.Audit;

/// <summary>
/// Audit and deletion log settings, bound from <c>Compliance:Audit</c>.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:Audit";

    /// <summary>
    /// How long audit and deletion-log entries are kept, in years. Default 7. Entries older than this are
    /// removed by <see cref="LogRetentionJob"/>; 0 keeps them forever.
    /// </summary>
    public int LogRetentionYears { get; set; } = 7;

    /// <summary>How often the log retention job runs. Default daily.</summary>
    public TimeSpan LogRetentionJobPeriod { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Delay after startup before the first run. Default 15 minutes.</summary>
    public TimeSpan LogRetentionJobDelay { get; set; } = TimeSpan.FromMinutes(15);
}
