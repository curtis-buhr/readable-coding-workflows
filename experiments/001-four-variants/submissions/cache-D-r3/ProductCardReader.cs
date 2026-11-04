namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

/// <summary>Describes the outcome for one requested product key.</summary>
/// <param name="RequestedKey">The original input, including its spelling and whitespace.</param>
/// <param name="Card">The loaded card, or null when the request failed.</param>
/// <param name="Error">The error code, or null when the request succeeded.</param>
public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error)
{
    /// <summary>Whether this successful result used stale data after catalog unavailability.</summary>
    public bool IsStale { get; init; }
}

/// <summary>Reads cards by canonical catalog identity while retaining each requested key's spelling.</summary>
/// <param name="cache">The cache used to retrieve product descriptions.</param>
public sealed class ProductCardReader(CatalogCache cache)
{
    /// <summary>Reads a fresh card by canonical key while preserving the original requested spelling.</summary>
    /// <param name="requestedKey">The key, trimmed and converted to uppercase invariantly for the cache lookup.</param>
    /// <returns>A card containing the original key and its cached or loaded description.</returns>
    /// <exception cref="ArgumentNullException">The requested key is null.</exception>
    /// <exception cref="ArgumentException">The requested key is empty or whitespace-only.</exception>
    /// <remarks>Cache and loader exceptions propagate to the caller.</remarks>
    public ProductCard Read(string requestedKey)
    {
        // The cache validates before normalizing, while the card retains the supplied spelling.
        return new ProductCard(requestedKey, cache.Get(requestedKey));
    }

    /// <summary>Reads fresh cards with one result per input occurrence, in enumeration order.</summary>
    /// <param name="keys">The requested keys, enumerated once.</param>
    /// <returns>Ordered outcomes from <see cref="ReadMany(IEnumerable{string}, FreshnessPolicy)"/> using the strict policy.</returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <remarks>Canonical identity, outcome sharing, and error handling match the policy overload.</remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys) =>
        ReadMany(keys, FreshnessPolicy.RequireFresh);

    /// <summary>Reads ordered card outcomes with the requested cache freshness policy.</summary>
    /// <param name="keys">The requested keys, enumerated once.</param>
    /// <param name="policy">Whether a transient refresh failure may yield an eligible stale card.</param>
    /// <returns>
    /// Results retaining each original key. Successful cards also retain that spelling.
    /// Null, empty, or whitespace-only keys have error "invalid-key". Catalog unavailability
    /// has error "unavailable". Failed results have no card and successful results have no error.
    /// Only successes obtained through stale fallback have <see cref="CardReadResult.IsStale"/> set.
    /// </returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is undefined; keys are not enumerated.</exception>
    /// <remarks>
    /// Keys are trimmed and converted to uppercase invariantly, then share their first outcome
    /// within this call using ordinal comparison, including stale status. Outcomes are retained
    /// only for this call, so later calls can retry failures or refresh stale data.
    /// Cache lookups may populate or refresh the shared cache. Only
    /// <see cref="CatalogUnavailableException"/> from a lookup becomes an error result.
    /// Other lookup exceptions and all input enumeration exceptions propagate.
    /// Earlier successful lookups can remain cached if a later operation throws.
    /// </remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys, FreshnessPolicy policy)
    {
        // Validate before touching the enumerable, including when it would produce no keys.
        if (policy is not FreshnessPolicy.RequireFresh and not FreshnessPolicy.AllowStaleOnTransientFailure)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The freshness policy is undefined.");

        ArgumentNullException.ThrowIfNull(keys);

        // Keep batch outcomes separate from the cache so failed lookups can be retried later.
        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, (string? Description, string? Error, bool IsStale)>(StringComparer.Ordinal);

        foreach (var requestedKey in keys)
        {
            // Invalid inputs bypass lookup and retain their original value in the result.
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new CardReadResult(requestedKey, null, "invalid-key"));
                continue;
            }

            // Remember the first outcome for each canonical key even if enumeration changes the cache.
            var canonicalKey = CatalogCache.CanonicalizeKey(requestedKey);
            if (!outcomes.TryGetValue(canonicalKey, out var outcome))
            {
                // The cache still owns loading and expiration. Only unavailability becomes a result.
                try
                {
                    var catalogValue = cache.Read(canonicalKey, policy);
                    outcome = (catalogValue.Value, null, catalogValue.IsStale);
                }
                catch (CatalogUnavailableException)
                {
                    outcome = (null, "unavailable", false);
                }

                outcomes.Add(canonicalKey, outcome);
            }

            // Share the complete outcome while giving every card its corresponding input key.
            var card = outcome.Error is null ? new ProductCard(requestedKey, outcome.Description!) : null;
            results.Add(new CardReadResult(requestedKey, card, outcome.Error) { IsStale = outcome.IsStale });
        }

        return results;
    }
}
