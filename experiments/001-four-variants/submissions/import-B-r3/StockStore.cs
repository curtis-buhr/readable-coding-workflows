namespace StockImport;

/// <summary>Stores nonnegative quantities by trimmed SKU and remembers committed import operations.</summary>
/// <remarks>All access, including imports, must be sequential.</remarks>
public sealed class StockStore
{
    // Updating an equivalent dictionary key retains the first inserted, trimmed spelling.
    private Dictionary<string, int> quantities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> committedOperationIds = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store with no committed import operations.</summary>
    public StockStore()
    {
    }

    /// <summary>Copies quantities, key spellings, and operation history for isolated import evaluation.</summary>
    /// <param name="source">The store whose state is copied without changing it.</param>
    private StockStore(StockStore source)
    {
        quantities = new Dictionary<string, int>(source.quantities, source.quantities.Comparer);
        committedOperationIds = new HashSet<string>(source.committedOperationIds, StringComparer.Ordinal);
    }

    /// <summary>Gets the quantity for a trimmed SKU using ordinal case-insensitive comparison.</summary>
    /// <param name="sku">A nonblank SKU; surrounding whitespace is ignored.</param>
    /// <returns>The stored quantity, or zero if the SKU has never been inserted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace only.</exception>
    public int GetQuantity(string sku)
    {
        string normalizedSku = NormalizeSku(sku);
        return quantities.GetValueOrDefault(normalizedSku);
    }

    /// <summary>Adds a signed delta to a trimmed SKU without recording an import operation ID.</summary>
    /// <param name="sku">A nonblank SKU matched ordinally without regard to case.</param>
    /// <param name="delta">The quantity to add, subtract, or leave unchanged.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace only.</exception>
    /// <exception cref="OverflowException">The resulting quantity exceeds the Int32 range.</exception>
    /// <exception cref="InvalidOperationException">The resulting quantity would be negative.</exception>
    /// <remarks>
    /// A successful adjustment inserts missing keys, even for zero deltas, preserving their first trimmed spelling.
    /// A failed adjustment leaves quantities and keys unchanged.
    /// </remarks>
    public void Adjust(string sku, int delta)
    {
        // Validate the complete result before inserting a key or changing its quantity.
        string normalizedSku = NormalizeSku(sku);
        int resultingQuantity = checked(quantities.GetValueOrDefault(normalizedSku) + delta);
        if (resultingQuantity < 0)
        {
            throw new InvalidOperationException("Stock cannot become negative.");
        }

        quantities[normalizedSku] = resultingQuantity;
    }

    /// <summary>Copies quantities and first-inserted spellings into an independent snapshot.</summary>
    /// <returns>A dictionary whose lookups use ordinal case-insensitive comparison.</returns>
    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(quantities, quantities.Comparer);

    /// <summary>Creates an independent store for evaluating quantities and duplicate operation IDs.</summary>
    /// <returns>A copy whose subsequent changes do not affect this store.</returns>
    internal StockStore CreateTentativeCopy() => new StockStore(this);

    /// <summary>Checks whether an operation ID is already recorded, using ordinal case-sensitive comparison.</summary>
    /// <param name="operationId">The parsed, trimmed operation ID.</param>
    /// <returns>Whether the ID belongs to an accepted operation in this state.</returns>
    internal bool ContainsOperation(string operationId) => committedOperationIds.Contains(operationId);

    /// <summary>Records an accepted operation in the tentative store after its adjustment succeeds.</summary>
    /// <param name="operationId">The parsed, trimmed operation ID to remember if this state is committed.</param>
    internal void RecordOperation(string operationId) => committedOperationIds.Add(operationId);

    /// <summary>Publishes completed tentative quantities, key spellings, and operation history.</summary>
    /// <param name="tentativeStore">An independent evaluation store that must not be used after publication.</param>
    /// <remarks>Access must be sequential; publication performs no further validation or allocation.</remarks>
    internal void CommitFrom(StockStore tentativeStore)
    {
        // Transfer both prepared collections so publication cannot fail midway through applying rows.
        quantities = tentativeStore.quantities;
        committedOperationIds = tentativeStore.committedOperationIds;
    }

    /// <summary>Validates a public SKU argument and removes its surrounding whitespace.</summary>
    /// <param name="sku">The caller-supplied SKU.</param>
    /// <returns>The nonempty, trimmed SKU.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace only.</exception>
    private static string NormalizeSku(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return sku.Trim();
    }
}
