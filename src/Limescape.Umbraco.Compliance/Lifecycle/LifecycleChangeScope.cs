namespace Limescape.Umbraco.Compliance.Lifecycle;

/// <summary>
/// Marks the saves inside it as a lifecycle route of the package, so <see cref="LifecycleGuardHandler"/> lets them move a
/// protected status. Without a scope every route (backoffice, Management API, integrations, code) is refused. Flows with
/// the async context, like <see cref="Audit.PurgeScope"/>. Code in the same process can open one; the guard protects
/// against the outside routes, not against code that is trusted to run inside the host (tests and migrations use it).
/// </summary>
public sealed class LifecycleChangeScope : IDisposable
{
    private static readonly AsyncLocal<LifecycleChangeScope?> Ambient = new();

    private readonly LifecycleChangeScope? _parent;

    private LifecycleChangeScope(string route)
    {
        Route = route;
        _parent = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>The route in progress; see <see cref="LifecycleRoutes"/>.</summary>
    public string Route { get; }

    /// <summary>The route in progress, or null.</summary>
    public static LifecycleChangeScope? Current => Ambient.Value;

    /// <summary>True when the route in progress is <paramref name="route"/>.</summary>
    public static bool Is(string route) => string.Equals(Current?.Route, route, StringComparison.Ordinal);

    /// <summary>Starts a lifecycle route; dispose it when the save is done.</summary>
    public static LifecycleChangeScope Begin(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return new LifecycleChangeScope(route);
    }

    /// <inheritdoc />
    public void Dispose() => Ambient.Value = _parent;
}

/// <summary>Routes that may move a protected lifecycle status.</summary>
public static class LifecycleRoutes
{
    /// <summary>A reviewer's recorded rejection moves the status to rejected.</summary>
    public const string Rejection = "rejection";

    /// <summary>The archive route sets status archived and the archive date.</summary>
    public const string Archive = "archive";
}
