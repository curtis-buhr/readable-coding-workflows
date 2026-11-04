namespace ProductCatalog;

public sealed record ProductCard(string Key, string Description);

public sealed class ProductCardReader(CatalogCache cache)
{
    public ProductCard Read(string requestedKey) =>
        new(requestedKey, cache.Get(requestedKey.Trim()));
}
