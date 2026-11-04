namespace ProductCatalog;

/// <summary>Controls whether a failed refresh may return an eligible cached value.</summary>
public enum FreshnessPolicy
{
    /// <summary>Requires fresh data and propagates refresh failures.</summary>
    RequireFresh,

    /// <summary>Allows data younger than 90 seconds when a refresh reports catalog unavailability.</summary>
    AllowStaleOnTransientFailure
}
