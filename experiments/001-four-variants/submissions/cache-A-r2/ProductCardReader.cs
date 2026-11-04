namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error)
{
    public bool IsStale { get; init; }
}

public sealed class ProductCardReader(CatalogCache cache)
{
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));

    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys) =>
        ReadMany(keys, FreshnessPolicy.RequireFresh);

    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys, FreshnessPolicy policy)
    {
        if (policy is not FreshnessPolicy.RequireFresh and not FreshnessPolicy.AllowStaleOnTransientFailure)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown freshness policy.");

        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, CatalogValue?>(StringComparer.Ordinal);

        foreach (var requestedKey in keys)
        {
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new(requestedKey, null, "invalid-key"));
                continue;
            }

            var lookupKey = requestedKey.Trim();
            if (!outcomes.TryGetValue(lookupKey, out var value))
            {
                try
                {
                    value = cache.Read(lookupKey, policy);
                }
                catch (CatalogUnavailableException)
                {
                    value = null;
                }

                outcomes.Add(lookupKey, value);
            }

            results.Add(new CardReadResult(
                requestedKey,
                value is null ? null : new ProductCard(requestedKey, value.Value),
                value is null ? "unavailable" : null)
            {
                IsStale = value?.IsStale ?? false
            });
        }

        return results;
    }
}
