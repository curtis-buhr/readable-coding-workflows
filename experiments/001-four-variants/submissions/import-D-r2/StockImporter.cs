using System.Globalization;

namespace StockImport;

/// <summary>Evaluates and optionally applies stock adjustments from pipe-delimited rows.</summary>
public static class StockImporter
{
    /// <summary>Reads from the current input position and applies each accepted row to stock.</summary>
    /// <param name="input">The caller-owned reader containing operation ID, SKU, and delta fields.</param>
    /// <param name="store">The stock store updated by accepted rows.</param>
    /// <returns>Equal accepted and applied row counts, plus rejection issues in input order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// The reader is left open. Calls and other store access must be sequential.
    /// Reader failures propagate, and earlier accepted adjustments remain applied.
    /// Operation IDs are compared ordinally and are reserved only for this call.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store)
        => Run(input, store, ImportMode.ApplyPartial);

    /// <summary>Reads rows once, validating against prior accepted rows and applying the selected mode.</summary>
    /// <param name="input">The caller-owned reader, consumed from its current position and left open.</param>
    /// <param name="store">The initial stock, updated according to <paramref name="mode"/>.</param>
    /// <param name="mode">Whether to apply rows immediately, commit an issue-free import, or preview.</param>
    /// <returns>
    /// Accepted row count and issues in input order. AppliedRows equals AcceptedRows for partial
    /// imports and successful atomic imports, and is zero for rejected atomic imports and previews.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// Calls and other store access must be sequential. Reader failures propagate; atomic imports
    /// and previews leave the supplied store unchanged, while partial imports retain accepted changes.
    /// Operation IDs are compared ordinally and are reserved only for this call.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
    {
        // Reject unsupported modes before accessing either the reader or stock.
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        // A private copy lets speculative rows use the same stock validation as partial imports.
        StockStore evaluationStore = store;
        if (mode != ImportMode.ApplyPartial)
        {
            evaluationStore = new StockStore();
            CopyQuantities(store, evaluationStore);
        }

        // Keep duplicate tracking local so independent imports can reuse operation IDs.
        HashSet<string> acceptedOperationIds = new(StringComparer.Ordinal);
        List<ImportIssue> issues = new();
        int lineNumber = 0;
        int acceptedRows = 0;

        // Count every physical line while reading incrementally from the current position.
        while (input.ReadLine() is string line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Each nonblank row contributes either one accepted adjustment or one issue.
            string? issueCode = ApplyRow(line, evaluationStore, acceptedOperationIds);
            if (issueCode is null)
            {
                acceptedRows++;
            }
            else
            {
                issues.Add(new ImportIssue(lineNumber, issueCode));
            }
        }

        // Deferring the commit until EOF keeps reader failures from exposing tentative changes.
        int appliedRows = mode == ImportMode.ApplyPartial ? acceptedRows : 0;
        if (mode == ImportMode.ApplyAtomic && issues.Count == 0)
        {
            CopyQuantities(evaluationStore, store);
            appliedRows = acceptedRows;
        }

        return new ImportResult(acceptedRows, appliedRows, issues.AsReadOnly());
    }

    /// <summary>Copies source quantities into a destination, preserving destination-only keys.</summary>
    /// <param name="source">The stock whose quantities and explicit zero-valued keys are copied.</param>
    /// <param name="destination">The stock updated to match each source quantity.</param>
    /// <remarks>Both stores require sequential access throughout the copy.</remarks>
    private static void CopyQuantities(StockStore source, StockStore destination)
    {
        // Nonnegative Int32 quantities have a difference that fits Int32 and a valid final stock.
        // Adjust also copies explicit zero-valued keys that GetQuantity alone cannot distinguish.
        foreach (KeyValuePair<string, int> quantity in source.Snapshot())
        {
            int quantityDelta = quantity.Value - destination.GetQuantity(quantity.Key);
            destination.Adjust(quantity.Key, quantityDelta);
        }
    }

    /// <summary>Validates one row and applies its adjustment before reserving its operation ID.</summary>
    /// <param name="row">A nonblank input row to validate as three pipe-delimited fields.</param>
    /// <param name="store">The stock store to update if validation succeeds.</param>
    /// <param name="acceptedOperationIds">IDs accepted by this import, extended only on success.</param>
    /// <returns>A rejection code, or null after the adjustment and ID reservation succeed.</returns>
    private static string? ApplyRow(string row, StockStore store, HashSet<string> acceptedOperationIds)
    {
        // Validate the field layout before indexing or interpreting individual fields.
        string[] fields = row.Split('|');
        if (fields.Length != 3)
        {
            return "InvalidRow";
        }

        // Normalize surrounding whitespace while preserving case-sensitive identities.
        string operationId = fields[0].Trim();
        string sku = fields[1].Trim();
        if (operationId.Length == 0 || sku.Length == 0)
        {
            return "InvalidRow";
        }

        // Parse signed decimal quantities independently of the caller's current culture.
        if (!int.TryParse(fields[2].Trim(), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out int delta))
        {
            return "InvalidQuantity";
        }

        // Only structurally valid, parsed rows participate in duplicate detection.
        if (acceptedOperationIds.Contains(operationId))
        {
            return "DuplicateOperation";
        }

        // StockStore checks the range before mutation, keeping either rejection atomic.
        try
        {
            store.Adjust(sku, delta);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
        {
            return "QuantityOutOfRange";
        }

        // Reserve the ID after success so a rejected operation can be corrected later.
        acceptedOperationIds.Add(operationId);
        return null;
    }
}

/// <summary>Identifies a rejected import row and its reason.</summary>
/// <param name="LineNumber">The one-based physical line number, including ignored blank lines.</param>
/// <param name="Code">InvalidRow, InvalidQuantity, DuplicateOperation, or QuantityOutOfRange.</param>
public sealed record ImportIssue(int LineNumber, string Code);

/// <summary>Reports accepted adjustments and ordered row rejections from an import.</summary>
/// <param name="AcceptedRows">The number of rows accepted against stock including prior accepted rows.</param>
/// <param name="AppliedRows">AcceptedRows for partial or successful atomic imports; zero for rejected atomic imports and previews.</param>
/// <param name="Issues">Rejection issues in input order.</param>
public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
