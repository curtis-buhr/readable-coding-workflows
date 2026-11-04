namespace StockImport;

public sealed class StockStore
{
    private readonly Dictionary<string, int> quantities = new(StringComparer.Ordinal);

    public int GetQuantity(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return quantities.GetValueOrDefault(sku);
    }

    public void Adjust(string sku, int delta)
    {
        int next = checked(GetQuantity(sku) + delta);
        if (next < 0)
        {
            throw new InvalidOperationException("Stock cannot become negative.");
        }

        quantities[sku] = next;
    }

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(quantities, quantities.Comparer);
}
