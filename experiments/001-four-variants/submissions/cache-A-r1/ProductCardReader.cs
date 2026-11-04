namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

public sealed record CardReadResult(string? RequestedKey, ProductCard? Card, string? Error);

public sealed class ProductCardReader(CatalogCache cache)
{
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));

    public IReadOnlyList<CardReadResult> ReadMany(IEnumerable<string?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<CardReadResult>();
        var outcomes = new Dictionary<string, ProductCard?>(StringComparer.Ordinal);

        foreach (var requestedKey in keys)
        {
            if (string.IsNullOrWhiteSpace(requestedKey))
            {
                results.Add(new(requestedKey, null, "invalid-key"));
                continue;
            }

            var lookupKey = requestedKey.Trim();
            if (!outcomes.TryGetValue(lookupKey, out var card))
            {
                try
                {
                    card = new ProductCard(requestedKey, cache.Get(lookupKey));
                }
                catch (CatalogUnavailableException)
                {
                    card = null;
                }

                outcomes.Add(lookupKey, card);
            }

            results.Add(new(
                requestedKey,
                card is null ? null : new ProductCard(requestedKey, card.Description),
                card is null ? "unavailable" : null));
        }

        return results;
    }
}
