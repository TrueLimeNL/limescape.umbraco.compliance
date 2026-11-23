namespace Limescape.Umbraco.Compliance.FieldState;

/// <summary>
/// Settings of the field state rules, bound from <c>Compliance:FieldState</c>.
/// </summary>
public sealed class FieldStateOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:FieldState";

    /// <summary>
    /// Alias or name of the property group that holds archived fields. A property in a group with this alias or name
    /// (case-insensitive) is archived: its existing values stay, but no new value can be written. Default <c>Gearchiveerd</c>.
    /// </summary>
    public string ArchivedGroup { get; set; } = "Gearchiveerd";
}
