using System.Globalization;

namespace StockImport;

/// <summary>Evaluates stock adjustments in input order and applies them according to the import mode.</summary>
public static class StockImporter
{
    /// <summary>
    /// Reads from the current position and commits accepted operations after reading completes.
    /// Rejected rows produce one issue each without preventing other accepted rows from committing.
    /// </summary>
    /// <param name="input">The caller-owned reader, which is left open.</param>
    /// <param name="store">The initial stock, committed operation history, and destination for accepted changes.</param>
    /// <returns>Equal accepted and applied counts, with issues in physical line order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>Reader failures propagate unchanged and leave the store unchanged. Access must be sequential.</remarks>
    public static ImportResult Run(TextReader input, StockStore store)
        => Run(input, store, ImportMode.ApplyPartial, CancellationToken.None);

    /// <summary>
    /// Reads once from the current position, evaluating rows against all earlier accepted adjustments.
    /// Partial imports commit accepted rows after reading completes; atomic imports require no issues;
    /// previews never update the supplied store.
    /// </summary>
    /// <param name="input">The caller-owned reader, which is left open.</param>
    /// <param name="store">The initial stock and destination for any applied changes.</param>
    /// <param name="mode">Controls whether accepted changes commit despite issues, only without issues, or never.</param>
    /// <returns>Accepted and applied row counts, with one issue per rejected row in physical line order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown; input is not read.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <remarks>
    /// Reader exceptions propagate unchanged and leave the store unchanged in every mode.
    /// This overload uses no cancellation token. Calls and store access must be sequential.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
        => Run(input, store, mode, CancellationToken.None);

    /// <summary>
    /// Streams rows against tentative quantities and operation history, then commits according to the mode.
    /// Cancellation or a reader exception leaves quantities, key spellings, and operation history unchanged.
    /// </summary>
    /// <param name="input">The caller-owned reader, read once from its current position and left open.</param>
    /// <param name="store">The initial stock and committed operation history, and destination for committed changes.</param>
    /// <param name="mode">Partial commits accepted rows despite issues; atomic requires no issues; preview never commits.</param>
    /// <param name="cancellationToken">Checked before and after every line read, including EOF, and before publication.</param>
    /// <returns>Accepted and applied row counts, with one issue per rejected row in physical line order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unknown; input is not read.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="store"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    /// <remarks>
    /// Only committed operations reserve IDs in the supplied store. Reader exceptions propagate unchanged.
    /// Calls and store access must be sequential. Cancellation does not interrupt a running ReadLine call.
    /// </remarks>
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode, CancellationToken cancellationToken)
    {
        // Reject unknown modes before touching the reader or preparing tentative stock.
        if (mode is not (ImportMode.ApplyPartial or ImportMode.ApplyAtomic or ImportMode.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);

        // Stage every mode so cancellation and reader failures cannot publish even partial changes.
        cancellationToken.ThrowIfCancellationRequested();
        StockStore evaluationStore = store.CreateTentativeCopy();

        var issues = new List<ImportIssue>();
        int acceptedRows = 0;
        int lineNumber = 0;

        // Check both sides of each read because the reader can request cancellation, even at EOF.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = input.ReadLine();
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                break;
            }

            // Blank lines do not produce issues, but still contribute to physical line numbers.
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

            // Parsing takes precedence over IDs from either prior commits or this tentative evaluation.
            if (evaluationStore.ContainsOperation(operationId))
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

            // Remember accepted IDs only in the staged state; rejected rows leave them available.
            evaluationStore.RecordOperation(operationId);
            acceptedRows++;
        }

        // Prepare the result before publication so all potentially allocating work stays tentative.
        bool shouldCommit = mode == ImportMode.ApplyPartial || (mode == ImportMode.ApplyAtomic && issues.Count == 0);
        int appliedRows = shouldCommit ? acceptedRows : 0;
        var result = new ImportResult(acceptedRows, appliedRows, issues.AsReadOnly());

        // Publishing transfers the prepared state only after the final cancellation checkpoint.
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldCommit)
        {
            store.CommitFrom(evaluationStore);
        }

        return result;
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
