using System.Globalization;

namespace StockImport;

public enum ImportMode { ApplyPartial, ApplyAtomic, Preview }

public static class StockImporter
{
    public static ImportResult Run(TextReader input, StockStore store)
        => Run(input, store, ImportMode.ApplyPartial);

    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
        => Run(input, store, mode, CancellationToken.None);

    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        var evaluationStore = store.CreateWorkingCopy();

        var issues = new List<ImportIssue>();
        int acceptedRows = 0;
        int lineNumber = 0;

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
            string quantity = fields[2].Trim();
            if (operationId.Length == 0 || sku.Length == 0)
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidRow"));
                continue;
            }

            if (!int.TryParse(quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
            {
                issues.Add(new ImportIssue(lineNumber, "InvalidQuantity"));
                continue;
            }

            if (evaluationStore.HasOperation(operationId))
            {
                issues.Add(new ImportIssue(lineNumber, "DuplicateOperation"));
                continue;
            }

            try
            {
                evaluationStore.Adjust(sku, delta);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                issues.Add(new ImportIssue(lineNumber, "QuantityOutOfRange"));
                continue;
            }

            evaluationStore.RecordOperation(operationId);
            acceptedRows++;
        }

        int appliedRows = 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == ImportMode.ApplyPartial || (mode == ImportMode.ApplyAtomic && issues.Count == 0))
        {
            store.CommitFrom(evaluationStore);
            appliedRows = acceptedRows;
        }

        return new ImportResult(acceptedRows, appliedRows, issues.AsReadOnly());
    }
}

public sealed record ImportIssue(int LineNumber, string Code);

public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
