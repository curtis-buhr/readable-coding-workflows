namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

/// <summary>Describes the outcome for one requested product key.</summary>
/// <param name="RequestedKey">The original input, including its spelling and whitespace.</param>
/// <param name="Card">The loaded card, or null when the request failed.</param>
/// <param name="Error">The error code, or null when the request succeeded.</param>
public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error);

/// <summary>Reads product cards through a shared catalog cache.</summary>
/// <param name="cache">The cache used to retrieve product descriptions.</param>
public sealed class ProductCardReader(CatalogCache cache)
{
    /// <summary>Reads a card using a trimmed lookup key and the original requested spelling.</summary>
    /// <param name="requestedKey">The key whose trimmed value is looked up in the cache.</param>
    /// <returns>A card containing the original key and its cached or loaded description.</returns>
    /// <exception cref="NullReferenceException">The requested key is null.</exception>
    /// <exception cref="ArgumentException">The trimmed key is empty.</exception>
    /// <remarks>Cache and loader exceptions propagate to the caller.</remarks>
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));

    /// <summary>Reads one result per input occurrence, in enumeration order.</summary>
    /// <param name="keys">The requested keys, enumerated once.</param>
    /// <returns>
    /// Results retaining each original key. Successful cards also retain that spelling.
    /// Null, empty, or whitespace-only keys have error "invalid-key". Catalog unavailability
    /// has error "unavailable". Failed results have no card and successful results have no error.
    /// </returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <remarks>
    /// Trimmed keys share their first outcome within this call using ordinal, case-sensitive
    /// comparison. Outcomes are retained only for this call, so later calls can retry failures.
    /// Cache lookups may populate or refresh the shared cache. Only
    /// <see cref="CatalogUnavailableException"/> from a lookup becomes an error result.
    /// Other lookup exceptions and all input enumeration exceptions propagate.
    /// Earlier successful lookups can remain cached if a later operation throws.
    /// </remarks>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Keep batch outcomes separate from the cache so failed lookups can be retried later.
        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, (string? Description, string? Error)>(StringComparer.Ordinal);

        foreach (var requestedKey in keys)
        {
            // Invalid inputs bypass lookup and retain their original value in the result.
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new CardReadResult(requestedKey, null, "invalid-key"));
                continue;
            }

            // Remember the first normalized outcome even if the cache changes during enumeration.
            var lookupKey = requestedKey.Trim();
            if (!outcomes.TryGetValue(lookupKey, out var outcome))
            {
                // The cache still owns loading and expiration. Only unavailability becomes a result.
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

            // Share the description or error while giving every card its corresponding input key.
            var card = outcome.Error is null ? new ProductCard(requestedKey, outcome.Description!) : null;
            results.Add(new CardReadResult(requestedKey, card, outcome.Error));
        }

        return results;
    }
}
