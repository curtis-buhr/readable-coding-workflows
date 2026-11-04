using System.Globalization;

namespace StockImport;

/// <summary>Evaluates and optionally applies stock adjustments from pipe-delimited rows.</summary>
public static class StockImporter
{
    /// <summary>Reads from the current input position and commits accepted rows after successful reading.</summary>
    /// <param name="input">The caller-owned reader containing operation ID, SKU, and delta fields.</param>
    /// <param name="store">The stock store updated by accepted rows.</param>
    /// <returns>Equal accepted and applied row counts, plus rejection issues in input order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// The reader is left open. Calls and other store access must be sequential.
    /// Reader failures propagate without changing the store. Row issues do not prevent accepted rows
    /// from committing. Accepted operation IDs remain reserved in this store, compared ordinally
    /// and case-sensitively. This overload uses CancellationToken.None.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store)
        => Run(input, store, ImportMode.ApplyPartial, CancellationToken.None);

    /// <summary>Reads rows once, validating against prior accepted rows and applying the selected mode.</summary>
    /// <param name="input">The caller-owned reader, consumed from its current position and left open.</param>
    /// <param name="store">The initial stock, updated according to <paramref name="mode"/>.</param>
    /// <param name="mode">Whether to commit accepted rows, commit only an issue-free import, or preview.</param>
    /// <returns>
    /// Accepted row count and issues in input order. AppliedRows equals AcceptedRows for partial
    /// imports and successful atomic imports, and is zero for rejected atomic imports and previews.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// Calls and other store access must be sequential. Reader failures leave the store unchanged in
    /// every mode. Committed operation IDs remain reserved in this store, compared ordinally and
    /// case-sensitively. This overload uses CancellationToken.None.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
        => Run(input, store, mode, CancellationToken.None);

    /// <summary>Streams rows against tentative stock and publishes changes according to the mode.</summary>
    /// <param name="input">The caller-owned reader, consumed from its current position and left open.</param>
    /// <param name="store">The stock and operation history used for validation and any commit.</param>
    /// <param name="mode">Whether to commit accepted rows, require no row issues, or only preview.</param>
    /// <param name="cancellationToken">Checked before and after each read and before publication.</param>
    /// <returns>
    /// Accepted row count and issues in input order. AppliedRows equals AcceptedRows for partial
    /// imports and successful atomic imports, and is zero for rejected atomic imports and previews.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown, checked first.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed for <paramref name="cancellationToken"/>.</exception>
    /// <remarks>
    /// Access must be sequential. Cancellation and reader failures leave quantities, SKU spellings,
    /// and operation history unchanged in every mode; reader exceptions propagate unchanged.
    /// Partial imports commit accepted rows even when other rows have issues. Previews and rejected
    /// atomic imports reserve no IDs. Committed IDs compare ordinally and case-sensitively.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode,
        CancellationToken cancellationToken)
    {
        // Reject unsupported modes before accessing either the reader or stock.
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        // Stage quantities and ID history in every mode so cancellation or reader failure can discard both.
        cancellationToken.ThrowIfCancellationRequested();
        StockStore evaluationStore = store.CreateImportCopy();

        // Retain counts and issues while consuming rows individually, without buffering the input.
        List<ImportIssue> issues = new();
        int lineNumber = 0;
        int acceptedRows = 0;

        // Count every physical line while reading incrementally from the current position.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = input.ReadLine();
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                break;
            }

            // Increment before skipping blanks to keep issue locations tied to physical input.
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Each nonblank row contributes either one accepted adjustment or one issue.
            string? issueCode = ApplyRow(line, evaluationStore);
            if (issueCode is null)
            {
                acceptedRows++;
            }
            else
            {
                issues.Add(new ImportIssue(lineNumber, issueCode));
            }
        }

        // Build the result before publication so committing only transfers already prepared state.
        bool shouldCommit = mode == ImportMode.ApplyPartial
            || (mode == ImportMode.ApplyAtomic && issues.Count == 0);
        int appliedRows = shouldCommit ? acceptedRows : 0;
        ImportResult result = new(acceptedRows, appliedRows, issues.AsReadOnly());

        // No cancellation checks follow publication: a canceled call must leave the store untouched.
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldCommit)
        {
            store.CommitImport(evaluationStore);
        }

        return result;
    }

    /// <summary>Validates one row and applies its adjustment before reserving its operation ID.</summary>
    /// <param name="row">A nonblank input row to validate as three pipe-delimited fields.</param>
    /// <param name="store">The detached stock and accepted-ID history updated only on row success.</param>
    /// <returns>A rejection code, or null after the adjustment and ID reservation succeed.</returns>
    private static string? ApplyRow(string row, StockStore store)
    {
        // Validate the field layout before indexing or interpreting individual fields.
        string[] fields = row.Split('|');
        if (fields.Length != 3)
        {
            return "InvalidRow";
        }

        // IDs retain case-sensitive identity; StockStore resolves normalized SKU variants together.
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
        if (store.HasAcceptedOperation(operationId))
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
        store.RememberAcceptedOperation(operationId);
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
