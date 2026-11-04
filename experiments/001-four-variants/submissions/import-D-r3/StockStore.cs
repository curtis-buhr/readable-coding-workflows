namespace StockImport;

/// <summary>Holds nonnegative quantities by trimmed, ordinal case-insensitive SKU.</summary>
/// <remarks>
/// Each key retains its first successfully inserted trimmed spelling. Accepted import operation
/// IDs belong to this store; direct adjustments do not add IDs. All access must be sequential.
/// </remarks>
public sealed class StockStore
{
    // Detached import copies own both collections until a completed import publishes them.
    private Dictionary<string, int> quantities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> acceptedOperationIds = new(StringComparer.Ordinal);

    /// <summary>Returns the quantity for a trimmed SKU, comparing case-insensitively.</summary>
    /// <param name="sku">A nonblank SKU; surrounding whitespace is ignored.</param>
    /// <returns>The stored quantity, or zero when the SKU is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace-only.</exception>
    public int GetQuantity(string sku) => quantities.GetValueOrDefault(NormalizeSku(sku));

    /// <summary>Adds a signed delta without allowing negative stock or integer overflow.</summary>
    /// <param name="sku">A nonblank SKU, trimmed and compared using ordinal case-insensitive comparison.</param>
    /// <param name="delta">The signed change in stock units; zero may establish a new key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace-only.</exception>
    /// <exception cref="OverflowException">The resulting quantity exceeds the Int32 range.</exception>
    /// <exception cref="InvalidOperationException">The resulting quantity is negative.</exception>
    /// <remarks>Rejection leaves stock unchanged. Success preserves an existing key's spelling.</remarks>
    public void Adjust(string sku, int delta)
    {
        // Validate the complete change before writing so rejection cannot establish a spelling.
        string normalizedSku = NormalizeSku(sku);
        int nextQuantity = checked(quantities.GetValueOrDefault(normalizedSku) + delta);
        if (nextQuantity < 0)
        {
            throw new InvalidOperationException("Stock cannot become negative.");
        }

        // Updating through the comparer retains the first key's trimmed spelling.
        quantities[normalizedSku] = nextQuantity;
    }

    /// <summary>Copies quantities and their first-inserted trimmed SKU spellings.</summary>
    /// <returns>An independent dictionary whose lookups use ordinal case-insensitive comparison.</returns>
    public IReadOnlyDictionary<string, int> Snapshot()
        => new Dictionary<string, int>(quantities, quantities.Comparer);

    /// <summary>Creates detached stock and operation history for tentative import evaluation.</summary>
    /// <returns>A private copy preserving quantities, key spellings, and accepted operation IDs.</returns>
    internal StockStore CreateImportCopy()
    {
        // Copy dictionary entries directly to retain explicit zero keys and their original spelling.
        return new StockStore
        {
            quantities = new Dictionary<string, int>(quantities, quantities.Comparer),
            acceptedOperationIds = new HashSet<string>(acceptedOperationIds, acceptedOperationIds.Comparer)
        };
    }

    /// <summary>Checks committed and tentatively accepted IDs using ordinal case-sensitive comparison.</summary>
    /// <param name="operationId">The parsed, trimmed operation ID.</param>
    /// <returns>Whether this state already contains the accepted ID.</returns>
    internal bool HasAcceptedOperation(string operationId) => acceptedOperationIds.Contains(operationId);

    /// <summary>Records an ID after its adjustment succeeds in a detached import copy.</summary>
    /// <param name="operationId">The parsed, trimmed ID of an accepted operation.</param>
    internal void RememberAcceptedOperation(string operationId) => acceptedOperationIds.Add(operationId);

    /// <summary>Publishes an import's privately evaluated quantities and operation history.</summary>
    /// <param name="evaluationStore">A detached copy that must not be used after publication.</param>
    /// <remarks>Requires sequential access and completed validation and cancellation checks.</remarks>
    internal void CommitImport(StockStore evaluationStore)
    {
        // Transfer ownership without allocation or further checks that could interrupt publication.
        quantities = evaluationStore.quantities;
        acceptedOperationIds = evaluationStore.acceptedOperationIds;
    }

    /// <summary>Validates a SKU argument and removes surrounding whitespace.</summary>
    /// <param name="sku">The caller's SKU spelling.</param>
    /// <returns>The nonempty, trimmed spelling used for dictionary access.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sku"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is empty or whitespace-only.</exception>
    private static string NormalizeSku(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return sku.Trim();
    }
}
