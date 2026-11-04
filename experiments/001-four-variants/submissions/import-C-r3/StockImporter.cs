using System.Globalization;

namespace StockImport;

public static class StockImporter
{
    public static ImportResult Run(TextReader input, StockStore store) =>
        Run(input, store, ImportMode.ApplyPartial, CancellationToken.None);

    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode) =>
        Run(input, store, mode, CancellationToken.None);

    /// <summary>
    /// Reads rows once, in order, against tentative stock and operation IDs.
    /// Changes are committed according to the mode after reading completes;
    /// cancellation or a reader failure leaves the supplied store unchanged.
    /// </summary>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        StockStore workingStore = store.CreateWorkingCopy();
        var issues = new List<ImportIssue>();
        int lineNumber = 0;
        int acceptedRows = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = input.ReadLine();
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                break;
            }

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

            if (workingStore.ContainsOperation(operationId))
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

            workingStore.RecordOperation(operationId);
            acceptedRows++;
        }

        bool shouldCommit = mode == ImportMode.ApplyPartial ||
            (mode == ImportMode.ApplyAtomic && issues.Count == 0);
        var result = new ImportResult(acceptedRows, shouldCommit ? acceptedRows : 0, issues.AsReadOnly());

        cancellationToken.ThrowIfCancellationRequested();
        if (shouldCommit)
        {
            store.CommitFrom(workingStore);
        }

        return result;
    }
}
