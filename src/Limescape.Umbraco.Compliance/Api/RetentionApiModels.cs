namespace Limescape.Umbraco.Compliance.Api;

/// <summary>Body for creating or updating a retention class. Enum values are camelCase strings.</summary>
public sealed class RetentionClassRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public int DurationYears { get; set; }

    public int DurationMonths { get; set; }

    public int DurationDays { get; set; }

    /// <summary>archived, published, lastModified or dateProperty.</summary>
    public string? StartEvent { get; set; }

    /// <summary>Required for dateProperty.</summary>
    public string? StartPropertyAlias { get; set; }

    /// <summary>allowDeletion or notify.</summary>
    public string? EndAction { get; set; }
}

/// <summary>Body for creating an assignment.</summary>
public sealed class RetentionAssignmentRequest
{
    public Guid ClassKey { get; set; }

    /// <summary>contentType, propertyValue or item.</summary>
    public string? Target { get; set; }

    public Guid? ContentTypeKey { get; set; }

    public string? PropertyAlias { get; set; }

    public string? PropertyValue { get; set; }

    public Guid? ItemKey { get; set; }
}
