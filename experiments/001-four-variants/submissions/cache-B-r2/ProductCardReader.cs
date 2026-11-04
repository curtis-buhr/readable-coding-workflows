namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

/// <summary>Describes the card or expected error for one requested key.</summary>
/// <param name="RequestedKey">The original input, including its spelling and whitespace.</param>
/// <param name="Card">The loaded card, or null when the read failed.</param>
/// <param name="Error">Null on success, "invalid-key" for blank input, or "unavailable" for a catalog outage.</param>
public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error)
{
    /// <summary>True when a successful card uses stale data after a transient refresh failure.</summary>
    public bool IsStale { get; init; }
}

/// <summary>Reads product cards using cached catalog descriptions.</summary>
/// <param name="cache">The cache used to retrieve descriptions.</param>
public sealed class ProductCardReader(CatalogCache cache)
{
    /// <summary>Looks up a trimmed key and preserves the requested spelling on the card.</summary>
    /// <param name="requestedKey">The original key to look up.</param>
    /// <returns>A card containing the original key and the cached or loaded description.</returns>
    /// <remarks>Cache lookup and loader exceptions propagate to the caller.</remarks>
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));

    /// <summary>Reads keys once in input order with strict freshness, sharing duplicate outcomes.</summary>
    /// <param name="keys">The requested keys, whose original spelling is retained in each result.</param>
    /// <returns>One result per occurrence, with a card or an "invalid-key" or "unavailable" error.</returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <remarks>Equivalent to <see cref="ReadMany(IEnumerable{string}, FreshnessPolicy)"/> with RequireFresh.</remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys) =>
        ReadMany(keys, FreshnessPolicy.RequireFresh);

    /// <summary>Reads keys once in input order, sharing outcomes for ordinally equal trimmed keys.</summary>
    /// <param name="keys">The requested keys, whose original spelling is retained in each result.</param>
    /// <param name="policy">Controls whether transient refresh outages may produce stale successes.</param>
    /// <returns>
    /// One result per occurrence, with a card or an "invalid-key" or "unavailable" error.
    /// Only successful stale fallbacks have IsStale true. Duplicates share the first outcome and stale flag.
    /// </returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is undefined; the collection is not enumerated.</exception>
    /// <remarks>
    /// Successful lookups can populate the cache. Unavailable outcomes are shared only within this call.
    /// Exceptions other than catalog outages during lookup propagate, as do collection enumeration exceptions.
    /// </remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys, FreshnessPolicy policy)
    {
        // Reject unknown policies even for empty input, before enumeration can have side effects.
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown freshness policy.");
        ArgumentNullException.ThrowIfNull(keys);

        // Keep the complete first outcome local: duplicates must not retry or observe changed freshness.
        var outcomes = new Dictionary<string, (string? Description, string? Error, bool IsStale)>(StringComparer.Ordinal);
        var results = new List<CardReadResult>();
        foreach (var requestedKey in keys)
        {
            // Reject blank inputs before trimming or consulting the cache.
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new CardReadResult(requestedKey, null, "invalid-key"));
                continue;
            }

            // Use the single reader's lookup spelling and retain the first outcome even if the cache changes.
            var lookupKey = requestedKey.Trim();
            if (!outcomes.TryGetValue(lookupKey, out var outcome))
            {
                // Only catalog outages become results. Other lookup failures keep their exception behavior.
                try
                {
                    var catalogValue = cache.Read(lookupKey, policy);
                    outcome = (catalogValue.Value, null, catalogValue.IsStale);
                }
                catch (CatalogUnavailableException)
                {
                    outcome = (null, "unavailable", false);
                }

                outcomes.Add(lookupKey, outcome);
            }

            // Reuse the outcome while giving each successful occurrence its own original key spelling.
            var card = outcome.Error is null
                ? new ProductCard(requestedKey, outcome.Description!)
                : null;
            results.Add(new CardReadResult(requestedKey, card, outcome.Error) { IsStale = outcome.IsStale });
        }

        return results;
    }
}
