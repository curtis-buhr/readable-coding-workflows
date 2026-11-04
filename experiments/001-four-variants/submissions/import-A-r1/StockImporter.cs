using System.Globalization;

namespace StockImport;

public static class StockImporter
{
    public static ImportResult Run(TextReader input, StockStore store)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        var acceptedOperations = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<ImportIssue>();
        int acceptedRows = 0;
        int lineNumber = 0;

        string? line;
        while ((line = input.ReadLine()) is not null)
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

            if (acceptedOperations.Contains(operationId))
            {
                issues.Add(new ImportIssue(lineNumber, "DuplicateOperation"));
                continue;
            }

            try
            {
                store.Adjust(sku, delta);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                issues.Add(new ImportIssue(lineNumber, "QuantityOutOfRange"));
                continue;
            }

            acceptedOperations.Add(operationId);
            acceptedRows++;
        }

        return new ImportResult(acceptedRows, acceptedRows, issues.AsReadOnly());
    }
}

public sealed record ImportIssue(int LineNumber, string Code);

public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
