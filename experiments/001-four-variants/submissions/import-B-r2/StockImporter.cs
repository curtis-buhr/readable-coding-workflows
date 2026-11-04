using System.Globalization;

namespace StockImport;

/// <summary>Evaluates stock adjustments in input order and applies them according to the import mode.</summary>
public static class StockImporter
{
    /// <summary>
    /// Reads from the current position and applies each valid, unique operation to the store.
    /// Rejected rows produce one issue each; earlier accepted adjustments remain applied if reading fails.
    /// </summary>
    /// <param name="input">The caller-owned reader, which is left open.</param>
    /// <param name="store">The store updated as rows are accepted.</param>
    /// <returns>Equal accepted and applied counts, with issues in physical line order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>Reader failures propagate. Calls and store access must be sequential.</remarks>
    public static ImportResult Run(TextReader input, StockStore store)
        => Run(input, store, ImportMode.ApplyPartial);

    /// <summary>
    /// Reads once from the current position, evaluating rows against all earlier accepted adjustments.
    /// Partial imports apply rows immediately; atomic imports commit only when there are no issues;
    /// previews never update the supplied store.
    /// </summary>
    /// <param name="input">The caller-owned reader, which is left open.</param>
    /// <param name="store">The initial stock and destination for any applied changes.</param>
    /// <param name="mode">Controls whether accepted changes are applied immediately, together, or never.</param>
    /// <returns>Accepted and applied row counts, with one issue per rejected row in physical line order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown; input is not read.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// Reader exceptions propagate unchanged. Atomic imports and previews leave the store unchanged
    /// if reading fails; partial imports retain earlier accepted rows. Calls and store access must be sequential.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
    {
        // Reject unknown modes before touching the reader or preparing tentative stock.
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        // Isolate deferred changes so row rejection or reader failure cannot affect the supplied store.
        StockStore evaluationStore = mode == ImportMode.ApplyPartial ? store : CreateTentativeStore(store);

        // Only accepted IDs enter this call's ordinal duplicate tracker, including in previews.
        var acceptedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<ImportIssue>();
        int acceptedRows = 0;
        int lineNumber = 0;

        // Read one physical line at a time, including blank lines in issue locations.
        while (input.ReadLine() is string line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Preserve empty fields so malformed rows cannot silently shift their columns.
            string[] fields = line.Split('|');
            if (fields.Length != 3)
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidRow"));
                continue;
            }

            string operationId = fields[0].Trim();
            string sku = fields[1].Trim();
            string requestedDelta = fields[2].Trim();
            if (operationId.Length == 0 || sku.Length == 0)
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidRow"));
                continue;
            }

            // Allow an invariant leading sign, but no decimal points or grouping separators.
            if (!int.TryParse(requestedDelta, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int delta))
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidQuantity"));
                continue;
            }

            if (acceptedOperationIds.Contains(operationId))
            {
                issues.Add(new ImportIssue(lineNumber, "DuplicateOperation"));
                continue;
            }

            // StockStore validates before mutation; catch only its two quantity range failures.
            try
            {
                evaluationStore.Adjust(sku, delta);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                issues.Add(new ImportIssue(lineNumber, "QuantityOutOfRange"));
                continue;
            }

            // Reserve the ID after acceptance so a rejected operation can be retried later.
            acceptedOperationIds.Add(operationId);
            acceptedRows++;
        }

        // Reaching EOF permits an atomic commit only when every nonblank row was accepted.
        int appliedRows = mode == ImportMode.ApplyPartial ? acceptedRows : 0;
        if (mode == ImportMode.ApplyAtomic && issues.Count == 0)
        {
            ApplyTentativeQuantities(evaluationStore, store);
            appliedRows = acceptedRows;
        }

        return new ImportResult(acceptedRows, appliedRows, issues.AsReadOnly());
    }

    /// <summary>Copies all starting quantities and keys into an independent store for ordered evaluation.</summary>
    /// <param name="store">The source store, which is not modified.</param>
    /// <returns>A separate store containing the same ordinal SKU keys, including keys with zero stock.</returns>
    private static StockStore CreateTentativeStore(StockStore store)
    {
        // Starting from a snapshot preserves existing zero-quantity keys as well as available stock.
        var tentativeStore = new StockStore();
        foreach (KeyValuePair<string, int> quantity in store.Snapshot())
        {
            tentativeStore.Adjust(quantity.Key, quantity.Value);
        }

        return tentativeStore;
    }

    /// <summary>Commits validated final quantities after an issue-free atomic import reaches EOF.</summary>
    /// <param name="tentativeStore">The evaluated stock, containing all original keys and accepted additions.</param>
    /// <param name="store">The destination store; access must remain sequential throughout the import.</param>
    private static void ApplyTentativeQuantities(StockStore tentativeStore, StockStore store)
    {
        // Both quantities are nonnegative Int32 values, so their difference fits a single adjustment.
        // Applying zero differences also creates newly accepted keys whose final quantity is zero.
        foreach (KeyValuePair<string, int> quantity in tentativeStore.Snapshot())
        {
            int quantityDelta = quantity.Value - store.GetQuantity(quantity.Key);
            store.Adjust(quantity.Key, quantityDelta);
        }
    }
}

/// <summary>Identifies one rejected row and the reason it was rejected.</summary>
/// <param name="LineNumber">The 1-based physical line number, including blank lines.</param>
/// <param name="Code">InvalidRow, InvalidQuantity, DuplicateOperation, or QuantityOutOfRange.</param>
public sealed record ImportIssue(int LineNumber, string Code);

/// <summary>Reports accepted and applied row counts and ordered issues from one import.</summary>
/// <param name="AcceptedRows">The number of accepted rows, including zero adjustments.</param>
/// <param name="AppliedRows">The accepted count for partial or successful atomic imports; otherwise zero.</param>
/// <param name="Issues">The rejected rows in input order.</param>
public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
