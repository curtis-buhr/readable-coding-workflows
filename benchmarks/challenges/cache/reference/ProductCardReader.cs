namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);
public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error)
{
#if REV2
    public bool IsStale { get; init; }
#endif
}

public sealed class ProductCardReader(CatalogCache cache)
{
    public ProductCard Read(string requestedKey) =>
#if REV3
        new(requestedKey, cache.Get(requestedKey));
#else
        new(requestedKey, cache.Get(requestedKey.Trim()));
#endif

#if REV2
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys) =>
        ReadMany(keys, FreshnessPolicy.RequireFresh);

    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys, FreshnessPolicy policy)
#else
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys)
#endif
    {
        ArgumentNullException.ThrowIfNull(keys);
#if REV2
        if (policy is not (FreshnessPolicy.RequireFresh or FreshnessPolicy.AllowStaleOnTransientFailure))
            throw new ArgumentOutOfRangeException(nameof(policy));
#endif

        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, CardReadResult>(StringComparer.Ordinal);
        foreach (var requestedKey in keys)
        {
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new CardReadResult(requestedKey, null, "invalid-key"));
                continue;
            }

#if REV3
            var lookup = CatalogCache.NormalizeKey(requestedKey);
#else
            var lookup = requestedKey.Trim();
#endif
            if (!outcomes.TryGetValue(lookup, out var outcome))
            {
                try
                {
#if REV2
                    var value = cache.Read(lookup, policy);
                    outcome = new CardReadResult(requestedKey, new ProductCard(requestedKey, value.Value), null)
                        { IsStale = value.IsStale };
#else
                    outcome = new CardReadResult(requestedKey, new ProductCard(requestedKey, cache.Get(lookup)), null);
#endif
                }
                catch (CatalogUnavailableException)
                {
                    outcome = new CardReadResult(requestedKey, null, "unavailable");
                }

                outcomes.Add(lookup, outcome);
            }

            var card = outcome.Card is null ? null : outcome.Card with { Key = requestedKey };
            results.Add(outcome with { RequestedKey = requestedKey, Card = card });
        }

        return results;
    }
}
