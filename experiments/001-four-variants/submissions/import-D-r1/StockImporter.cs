using System.Globalization;

namespace StockImport;

/// <summary>Applies stock adjustments from a stream of pipe-delimited rows.</summary>
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
    {
        // Validate both dependencies before reading input or changing stock.
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

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
            string? issueCode = ApplyRow(line, store, acceptedOperationIds);
            if (issueCode is null)
            {
                acceptedRows++;
            }
            else
            {
                issues.Add(new ImportIssue(lineNumber, issueCode));
            }
        }

        // Every accepted row has already updated stock; expose issues without a mutable list.
        return new ImportResult(acceptedRows, acceptedRows, issues.AsReadOnly());
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
/// <param name="AcceptedRows">The number of rows accepted by the import.</param>
/// <param name="AppliedRows">The number of applied adjustments; equals AcceptedRows in results returned by <see cref="StockImporter.Run"/>.</param>
/// <param name="Issues">Rejection issues in input order.</param>
public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
