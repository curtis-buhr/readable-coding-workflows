namespace StockImport;

public sealed class StockStore
{
    private Dictionary<string, int> quantities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> operationIds = new(StringComparer.Ordinal);

    public int GetQuantity(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return quantities.GetValueOrDefault(sku.Trim());
    }

    public void Adjust(string sku, int delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        sku = sku.Trim();
        int next = checked(quantities.GetValueOrDefault(sku) + delta);
        if (next < 0)
        {
            throw new InvalidOperationException("Stock cannot become negative.");
        }

        quantities[sku] = next;
    }

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(quantities, quantities.Comparer);

    internal StockStore CreateWorkingCopy() => new()
    {
        quantities = new Dictionary<string, int>(quantities, quantities.Comparer),
        operationIds = new HashSet<string>(operationIds, operationIds.Comparer)
    };

    internal bool HasOperation(string operationId) => operationIds.Contains(operationId);

    internal void RecordOperation(string operationId) => operationIds.Add(operationId);

    internal void CommitFrom(StockStore workingCopy)
    {
        // The importer relinquishes its working copy after publication.
        quantities = workingCopy.quantities;
        operationIds = workingCopy.operationIds;
    }
}
