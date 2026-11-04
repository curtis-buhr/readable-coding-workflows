namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

/// <summary>Describes the card or expected error for one requested key.</summary>
/// <param name="RequestedKey">The original input, including its spelling and whitespace.</param>
/// <param name="Card">The loaded card, or null when the read failed.</param>
/// <param name="Error">Null on success, "invalid-key" for blank input, or "unavailable" for a catalog outage.</param>
public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error);

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

    /// <summary>Reads keys once in input order, sharing outcomes for ordinally equal trimmed keys.</summary>
    /// <param name="keys">The requested keys, whose original spelling is retained in each result.</param>
    /// <returns>One result per occurrence, with a card or an "invalid-key" or "unavailable" error.</returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <remarks>
    /// Successful lookups can populate the cache. Unavailable outcomes are shared only within this call.
    /// Exceptions other than catalog outages during lookup propagate, as do collection enumeration exceptions.
    /// </remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Keep outcomes local so duplicates share this call's first lookup and later calls can retry outages.
        var outcomes = new Dictionary<string, (string? Description, string? Error)>(StringComparer.Ordinal);
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
                    outcome = (cache.Get(lookupKey), null);
                }
                catch (CatalogUnavailableException)
                {
                    outcome = (null, "unavailable");
                }

                outcomes.Add(lookupKey, outcome);
            }

            // Reuse the description while giving each successful occurrence its own original key spelling.
            var card = outcome.Error is null
                ? new ProductCard(requestedKey, outcome.Description!)
                : null;
            results.Add(new CardReadResult(requestedKey, card, outcome.Error));
        }

        return results;
    }
}
