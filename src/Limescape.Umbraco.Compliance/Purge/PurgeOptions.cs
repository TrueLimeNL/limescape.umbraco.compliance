namespace Limescape.Umbraco.Compliance.Purge;

/// <summary>
/// Settings of the purge and of the elements (media) of a material, bound from <c>Compliance:Purge</c>.
/// </summary>
public sealed class PurgeOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Compliance:Purge";

    /// <summary>
    /// Alias of the media picker property that holds the elements (media items) of a document. Default <c>elements</c>.
    /// Empty: documents have no elements for the package, and nothing below applies.
    /// </summary>
    public string? ElementsProperty { get; set; } = "elements";

    /// <summary>
    /// True (default): moving a document to the recycle bin also moves its elements there, and restoring the document
    /// restores them. An element that another document still uses stays where it is. This setting does not affect the
    /// purge, which always takes the elements of a purged document along unless they are shared, under retention or on legal hold.
    /// </summary>
    public bool ElementsFollowMaterial { get; set; } = true;
}
