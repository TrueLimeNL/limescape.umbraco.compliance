namespace Limescape.Umbraco.Compliance.Retention;

/// <summary>
/// Retention settings, bound from <c>Compliance:Retention</c>.
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:Retention";

    /// <summary>
    /// Alias of the date property that holds the archive date, used by the start event <see cref="RetentionStartEvent.Archived"/>.
    /// Default <c>archivedAt</c>.
    /// </summary>
    public string ArchivedDatePropertyAlias { get; set; } = "archivedAt";

    /// <summary>
    /// Aliases of user groups that may manage retention classes and assignments, besides administrators
    /// (for example a functional management group). Empty by default: administrators only.
    /// Confirming a shorter retention period always requires an administrator.
    /// </summary>
    public IList<string> ManagerGroups { get; } = [];

    /// <summary>How often the retention job runs. Default daily.</summary>
    public TimeSpan JobPeriod { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Delay after startup before the first run. Default 10 minutes.</summary>
    public TimeSpan JobDelay { get; set; } = TimeSpan.FromMinutes(10);
}
