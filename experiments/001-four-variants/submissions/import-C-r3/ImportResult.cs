namespace StockImport;

public sealed record ImportIssue(int LineNumber, string Code);

public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
