namespace ProductCatalog;

/// <summary>A catalog description together with the freshness of this read's result.</summary>
/// <param name="Value">The cached or newly loaded description.</param>
/// <param name="IsStale">Whether a failed refresh caused this read to return an older cached value.</param>
public sealed record CatalogValue(string Value, bool IsStale);
