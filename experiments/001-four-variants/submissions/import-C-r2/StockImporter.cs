using System.Globalization;

namespace StockImport;

public static class StockImporter
{
    public static ImportResult Run(TextReader input, StockStore store) =>
        Run(input, store, ImportMode.ApplyPartial);

    /// <summary>
    /// Reads rows once, in order. Atomic and preview modes evaluate accepted rows
    /// against tentative quantities and leave the supplied store unchanged if reading fails.
    /// </summary>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        StockStore workingStore = store;
        if (mode != ImportMode.ApplyPartial)
        {
            workingStore = new StockStore();
            foreach (var (sku, quantity) in store.Snapshot())
            {
                workingStore.Adjust(sku, quantity);
            }
        }

        var acceptedOperations = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<ImportIssue>();
        int lineNumber = 0;
        int acceptedRows = 0;

        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] fields = line.Split('|');
            if (fields.Length != 3)
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidRow"));
                continue;
            }

            string operationId = fields[0].Trim();
            string sku = fields[1].Trim();
            if (operationId.Length == 0 || sku.Length == 0)
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidRow"));
                continue;
            }

            if (!int.TryParse(fields[2].Trim(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out int delta))
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidQuantity"));
                continue;
            }

            if (acceptedOperations.Contains(operationId))
            {
                issues.Add(new ImportIssue(lineNumber, "DuplicateOperation"));
                continue;
            }

            try
            {
                workingStore.Adjust(sku, delta);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                issues.Add(new ImportIssue(lineNumber, "QuantityOutOfRange"));
                continue;
            }

            acceptedOperations.Add(operationId);
            acceptedRows++;
        }

        int appliedRows = mode == ImportMode.ApplyPartial ? acceptedRows : 0;
        if (mode == ImportMode.ApplyAtomic && issues.Count == 0)
        {
            foreach (var (sku, quantity) in workingStore.Snapshot())
            {
                // Both quantities are nonnegative Int32 values, so the difference fits in an Int32.
                store.Adjust(sku, quantity - store.GetQuantity(sku));
            }

            appliedRows = acceptedRows;
        }

        return new ImportResult(acceptedRows, appliedRows, issues.AsReadOnly());
    }
}
