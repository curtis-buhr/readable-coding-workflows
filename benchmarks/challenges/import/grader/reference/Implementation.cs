using System.Globalization;

namespace StockImport;

public sealed class StockStore
{
#if STAGE3
    private Dictionary<string, int> quantities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> committedIds = new(StringComparer.Ordinal);
#else
    private Dictionary<string, int> quantities = new(StringComparer.Ordinal);
#endif

    public int GetQuantity(string sku) => quantities.GetValueOrDefault(Key(sku));

    public void Adjust(string sku, int delta)
    {
        string key = Key(sku);
        int next = checked(quantities.GetValueOrDefault(key) + delta);
        if (next < 0) throw new InvalidOperationException();
        quantities[key] = next;
    }

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(quantities, quantities.Comparer);

    private static string Key(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
#if STAGE3
        return sku.Trim();
#else
        return sku;
#endif
    }

#if STAGE2
    internal StockStore Copy()
    {
        var copy = new StockStore { quantities = new(quantities, quantities.Comparer) };
#if STAGE3
        copy.committedIds = new(committedIds, committedIds.Comparer);
#endif
        return copy;
    }

    internal void ReplaceWith(StockStore copy)
    {
        quantities = copy.quantities;
#if STAGE3
        committedIds = copy.committedIds;
#endif
    }
#endif

#if STAGE3
    internal bool AlreadyApplied(string operationId) => committedIds.Contains(operationId);
    internal void Record(string operationId) => committedIds.Add(operationId);
#endif
}

#if STAGE1
public sealed record ImportIssue(int LineNumber, string Code);
public sealed record ImportResult(int AcceptedRows, int AppliedRows, IReadOnlyList<ImportIssue> Issues);
#endif
#if STAGE2
public enum ImportMode { ApplyPartial, ApplyAtomic, Preview }
#endif

#if STAGE1
public static class StockImporter
{
#if STAGE2
    public static ImportResult Run(TextReader input, StockStore store) => Run(input, store, ImportMode.ApplyPartial);
#endif
#if STAGE3
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode) => Run(input, store, mode, CancellationToken.None);

    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode, CancellationToken cancellationToken)
#elif STAGE2
    public static ImportResult Run(TextReader input, StockStore store, ImportMode mode)
#else
    public static ImportResult Run(TextReader input, StockStore store)
#endif
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);
#if STAGE2
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
#endif
#if STAGE3
        var target = store.Copy();
#elif STAGE2
        var target = mode == ImportMode.ApplyPartial ? store : store.Copy();
#else
        var target = store;
#endif
#if !STAGE3
        var ids = new HashSet<string>(StringComparer.Ordinal);
#endif
        var issues = new List<ImportIssue>();
        int lineNumber = 0;
        int accepted = 0;
        while (true)
        {
#if STAGE3
            cancellationToken.ThrowIfCancellationRequested();
#endif
            string? line = input.ReadLine();
#if STAGE3
            cancellationToken.ThrowIfCancellationRequested();
#endif
            if (line is null) break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split('|', StringSplitOptions.TrimEntries);
            string? issue;
            if (fields.Length != 3 || fields[0].Length == 0 || fields[1].Length == 0)
                issue = "InvalidRow";
            else if (!int.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int delta))
                issue = "InvalidQuantity";
#if STAGE3
            else if (target.AlreadyApplied(fields[0]))
#else
            else if (ids.Contains(fields[0]))
#endif
                issue = "DuplicateOperation";
            else
            {
                try
                {
                    target.Adjust(fields[1], delta);
#if STAGE3
                    target.Record(fields[0]);
#else
                    ids.Add(fields[0]);
#endif
                    accepted++;
                    issue = null;
                }
                catch (Exception error) when (error is OverflowException or InvalidOperationException)
                {
                    issue = "QuantityOutOfRange";
                }
            }
            if (issue is not null) issues.Add(new(lineNumber, issue));
        }

#if STAGE2
        bool publish = mode == ImportMode.ApplyPartial || mode == ImportMode.ApplyAtomic && issues.Count == 0;
#if STAGE3
        cancellationToken.ThrowIfCancellationRequested();
#endif
        if (publish && !ReferenceEquals(store, target)) store.ReplaceWith(target);
        return new(accepted, publish ? accepted : 0, issues.ToArray());
#else
        return new(accepted, accepted, issues.ToArray());
#endif
    }
}
#endif
