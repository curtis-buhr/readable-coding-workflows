using System.Globalization;

namespace StockImport;

/// <summary>Applies stock adjustments from a reader, preserving input order.</summary>
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
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        // Only successfully applied IDs enter this call's ordinal duplicate tracker.
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
                store.Adjust(sku, delta);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                issues.Add(new ImportIssue(lineNumber, "QuantityOutOfRange"));
                continue;
            }

            // Reserve the ID after applying so a rejected operation can be retried later.
            acceptedOperationIds.Add(operationId);
            acceptedRows++;
        }

        return new ImportResult(acceptedRows, acceptedRows, issues.AsReadOnly());
    }
}

/// <summary>Identifies one rejected row and the reason it was rejected.</summary>
/// <param name="LineNumber">The 1-based physical line number, including blank lines.</param>
/// <param name="Code">InvalidRow, InvalidQuantity, DuplicateOperation, or QuantityOutOfRange.</param>
public sealed record ImportIssue(int LineNumber, string Code);

/// <summary>Reports the applied row counts and ordered issues from one import.</summary>
/// <param name="AcceptedRows">The number of accepted rows, including zero adjustments.</param>
/// <param name="AppliedRows">The number of applied rows, equal to the accepted count.</param>
/// <param name="Issues">The rejected rows in input order.</param>
public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
