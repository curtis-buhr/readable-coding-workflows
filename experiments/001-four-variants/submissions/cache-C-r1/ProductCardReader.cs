namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error);

public sealed class ProductCardReader(CatalogCache cache)
{
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));

    /// <summary>
    /// Reads keys in order, sharing the first outcome for each trimmed key within this call.
    /// </summary>
    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, (string? Description, string? Error)>(StringComparer.Ordinal);

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
                    outcome = (cache.Get(lookupKey), null);
                }
                catch (CatalogUnavailableException)
                {
                    outcome = (null, "unavailable");
                }

                outcomes.Add(lookupKey, outcome);
            }

            var card = outcome.Error is null ? new ProductCard(requestedKey, outcome.Description!) : null;
            results.Add(new(requestedKey, card, outcome.Error));
        }

        return results;
    }
}
