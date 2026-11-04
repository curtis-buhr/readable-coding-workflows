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

    /// <summary>
    /// Reads fresh data in key order, sharing the first outcome for each trimmed key within this call.
    /// </summary>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys) =>
        ReadMany(keys, FreshnessPolicy.RequireFresh);

    /// <summary>
    /// Reads keys in order using the policy, sharing the first outcome and staleness for each trimmed key.
    /// </summary>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys, FreshnessPolicy policy)
    {
        if (policy is not (FreshnessPolicy.RequireFresh or FreshnessPolicy.AllowStaleOnTransientFailure))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown freshness policy.");

        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, (string? Description, string? Error, bool IsStale)>(StringComparer.Ordinal);

        foreach (var requestedKey in keys)
        {
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new(requestedKey, null, "invalid-key"));
                continue;
            }

            var lookupKey = requestedKey.Trim();
            if (!outcomes.TryGetValue(lookupKey, out var outcome))
            {
                try
                {
                    var value = cache.Read(lookupKey, policy);
                    outcome = (value.Value, null, value.IsStale);
                }
                catch (CatalogUnavailableException)
                {
                    outcome = (null, "unavailable", false);
                }

                outcomes.Add(lookupKey, outcome);
            }

            var card = outcome.Error is null ? new ProductCard(requestedKey, outcome.Description!) : null;
            results.Add(new(requestedKey, card, outcome.Error) { IsStale = outcome.IsStale });
        }

        return results;
    }
}
