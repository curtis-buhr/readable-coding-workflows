using System.Text.Json;
using StockImport;

var results = new List<CaseResult>();

Case("store.missing-and-snapshot", 0, () =>
{
    var store = new StockStore();
    Equal(0, store.GetQuantity("missing"));
    store.Adjust("A", 4);
    var snapshot = store.Snapshot();
    store.Adjust("A", -1);
    store.Adjust("B", 0);
    Equal(4, snapshot["A"]);
    Equal(1, snapshot.Count);
    Equal(3, store.GetQuantity("A"));
    Equal(0, store.Snapshot()["B"]);
});
Case("store.adjust-failures-are-atomic", 0, () =>
{
    var store = new StockStore();
    store.Adjust("A", int.MaxValue);
    Throws<OverflowException>(() => store.Adjust("A", 1));
    Throws<InvalidOperationException>(() => store.Adjust("B", -1));
    Throws<ArgumentException>(() => store.Adjust(" \t", 1));
    Equal(int.MaxValue, store.GetQuantity("A"));
    Equal(1, store.Snapshot().Count);
});

#if !STAGE3
Case("store.ordinal-case-sensitive-until-r3", 0, () =>
{
    var store = new StockStore();
    store.Adjust(" A ", 1);
    store.Adjust("A", 2);
    store.Adjust("a", 3);
    Equal(1, store.GetQuantity(" A "));
    Equal(2, store.GetQuantity("A"));
    Equal(3, store.GetQuantity("a"));
});
#endif

#if STAGE1
Case("import.stream-position-and-physical-lines", 1, () =>
{
    var store = new StockStore();
    using var input = new LineReader("skip", " \t", " op1 | A | +4 ", "bad row", "", "op2|A|-1", "op3|A|1.0");
    Equal("skip", input.ReadLine());
    var report = StockImporter.Run(input, store);
    Report(report, 2, 2, (3, "InvalidRow"), (6, "InvalidQuantity"));
    Equal(3, store.GetQuantity("A"));
    Equal(8, input.ReadCount);
    Equal(false, input.Disposed);
});
Case("import.validation-precedence", 1, () =>
{
    var store = new StockStore();
    var report = StockImporter.Run(new StringReader("ok|A|1\nok|A|not-a-number\n|A|z\nother||5\no|A|1|2\nok|A|3\nsp|A|1,000\nlarge|A|2147483648\nzero|A|-0"), store);
    Report(report, 2, 2, (2, "InvalidQuantity"), (3, "InvalidRow"), (4, "InvalidRow"), (5, "InvalidRow"), (6, "DuplicateOperation"), (7, "InvalidQuantity"), (8, "InvalidQuantity"));
    Equal(1, store.GetQuantity("A"));
});
Case("import.rejected-ids-can-retry-and-casing-is-ordinal", 1, () =>
{
    var store = new StockStore();
    var report = StockImporter.Run(new StringReader("r|A|-2\nr|A|2\nR|A|3\n r |A|9"), store);
    Report(report, 2, 2, (1, "QuantityOutOfRange"), (4, "DuplicateOperation"));
    Equal(5, store.GetQuantity("A"));
});
Case("import.overflow-and-ordering", 1, () =>
{
    var store = new StockStore();
    store.Adjust("A", int.MaxValue);
    var report = StockImporter.Run(new StringReader("a|A|1\nb|A|-2\na|A|2\nc|A|1"), store);
    Report(report, 2, 2, (1, "QuantityOutOfRange"), (4, "QuantityOutOfRange"));
    Equal(int.MaxValue, store.GetQuantity("A"));
});
Case("import.null-arguments", 1, () =>
{
    Throws<ArgumentNullException>(() => StockImporter.Run(null!, new StockStore()));
    using var reader = new LineReader("x|A|1");
    Throws<ArgumentNullException>(() => StockImporter.Run(reader, null!));
    Equal(0, reader.ReadCount);
});

#if !STAGE3
Case("import.cross-call-id-reuse-until-r3", 1, () =>
{
    var store = new StockStore();
    Report(StockImporter.Run(new StringReader("same|X|3"), store), 1, 1);
    Report(StockImporter.Run(new StringReader("same|X|2"), store), 1, 1);
    Equal(5, store.GetQuantity("X"));
});
#endif
#endif

#if STAGE2
Case("modes.preview-tentative-quantity-no-side-effects", 2, () =>
{
    var store = new StockStore();
    store.Adjust("A", 2);
    var report = StockImporter.Run(new StringReader("p|A|-2\nq|A|-1\nr|B|3\ns|B|-3"), store, ImportMode.Preview);
    Report(report, 3, 0, (2, "QuantityOutOfRange"));
    Equal(2, store.GetQuantity("A"));
    Equal(1, store.Snapshot().Count);
});
Case("modes.atomic-success-and-partial-compatibility", 2, () =>
{
    var store = new StockStore();
    Report(StockImporter.Run(new StringReader("a|A|4\nb|A|-3"), store, ImportMode.ApplyAtomic), 2, 2);
    Report(StockImporter.Run(new StringReader("c|A|-2\nd|B|5"), store, ImportMode.ApplyPartial), 1, 1, (1, "QuantityOutOfRange"));
    Equal(1, store.GetQuantity("A"));
    Equal(5, store.GetQuantity("B"));
});
Case("modes.atomic-failure-reports-all", 2, () =>
{
    var store = new StockStore();
    store.Adjust("A", 1);
    var report = StockImporter.Run(new StringReader("x|A|-1\nx|A|2\ny|A|-1\nz|B|3\n?"), store, ImportMode.ApplyAtomic);
    Report(report, 2, 0, (2, "DuplicateOperation"), (3, "QuantityOutOfRange"), (5, "InvalidRow"));
    Equal(1, store.GetQuantity("A"));
    Equal(1, store.Snapshot().Count);
});
Case("modes.reader-errors-are-atomic", 2, () =>
{
    foreach (var mode in new[] { ImportMode.ApplyAtomic, ImportMode.Preview })
    {
        var store = new StockStore();
        var failure = new IOException("reader failure");
        using var input = new LineReader("a|A|2") { BeforeRead = count => { if (count == 2) throw failure; } };
        var actual = Throws<IOException>(() => StockImporter.Run(input, store, mode));
        Equal(true, ReferenceEquals(failure, actual));
        Equal(0, store.Snapshot().Count);
    }
});
Case("modes.invalid-enum-checked-before-input", 2, () =>
{
    var store = new StockStore();
    using var input = new LineReader("a|A|2");
    Throws<ArgumentOutOfRangeException>(() => StockImporter.Run(input, store, (ImportMode)123));
    Equal(0, input.ReadCount);
    Equal(0, store.Snapshot().Count);
});
#endif

#if STAGE3
Case("identity.ordinal-sku-and-first-spelling", 3, () =>
{
    var store = new StockStore();
    store.Adjust("  Widget ", 4);
    store.Adjust("widget", -1);
    store.Adjust("i", 1);
    store.Adjust("İ", 2);
    Equal(3, store.GetQuantity(" WIDGET  "));
    Equal(1, store.GetQuantity("I"));
    Equal(2, store.GetQuantity("İ"));
    var snapshot = store.Snapshot();
    Equal(3, snapshot["wIdGeT"]);
    Equal(true, snapshot.Keys.Contains("Widget", StringComparer.Ordinal));
    Equal(3, snapshot.Count);
});
Case("identity.preview-and-atomic-use-normalized-tentative-store", 3, () =>
{
    var store = new StockStore();
    Report(StockImporter.Run(new StringReader("p|Gizmo|3\nq|GIZMO|-3\nr|gizmo|-1"), store, ImportMode.Preview), 2, 0, (3, "QuantityOutOfRange"));
    Report(StockImporter.Run(new StringReader("p|GIZMO|3\nq|gizmo|-1"), store, ImportMode.ApplyAtomic), 2, 2);
    Equal("GIZMO", store.Snapshot().Keys.Single());
    Equal(2, store.GetQuantity("gizmo"));
});
Case("identity.committed-ids-cross-overload-and-store-scoped", 3, () =>
{
    var store = new StockStore();
    Report(StockImporter.Run(new StringReader("a|X|3"), store, ImportMode.ApplyAtomic), 1, 1);
    Report(StockImporter.Run(new StringReader("a|Y|5\nA|Y|2"), store), 1, 1, (1, "DuplicateOperation"));
    Report(StockImporter.Run(new StringReader("A|Y|1"), store, ImportMode.Preview), 0, 0, (1, "DuplicateOperation"));
    Report(StockImporter.Run(new StringReader("a|Y|5"), new StockStore()), 1, 1);
    Equal(3, store.GetQuantity("X"));
    Equal(2, store.GetQuantity("Y"));
});
Case("identity.rollback-does-not-reserve-id-or-key", 3, () =>
{
    var store = new StockStore();
    Report(StockImporter.Run(new StringReader("same|X|3\nreject|X|-4"), store, ImportMode.ApplyAtomic), 1, 0, (2, "QuantityOutOfRange"));
    Report(StockImporter.Run(new StringReader("same|x|2\nreject|x|2"), store), 2, 2);
    Equal("x", store.Snapshot().Keys.Single());
    Equal(4, store.GetQuantity("x"));
});
Case("cancel.pre-cancel-does-not-read", 3, () =>
{
    using var source = new CancellationTokenSource();
    using var reader = new LineReader("a|A|2");
    var store = new StockStore();
    source.Cancel();
    var error = Throws<OperationCanceledException>(() => StockImporter.Run(reader, store, ImportMode.ApplyPartial, source.Token));
    Equal(source.Token, error.CancellationToken);
    Equal(0, reader.ReadCount);
    Equal(0, store.Snapshot().Count);
});
Case("cancel.during-row-and-eof-leaves-state-unchanged", 3, () =>
{
    foreach (var mode in Enum.GetValues<ImportMode>())
    foreach (int cancelAt in new[] { 2, 3 })
    {
        var store = new StockStore();
        store.Adjust("Sku", 5);
        using var source = new CancellationTokenSource();
        using var reader = new LineReader("a|SKU|-2", "b|NEW|2") { BeforeRead = count => { if (count == cancelAt) source.Cancel(); } };
        var error = Throws<OperationCanceledException>(() => StockImporter.Run(reader, store, mode, source.Token));
        Equal(source.Token, error.CancellationToken);
        Equal(cancelAt, reader.ReadCount);
        Equal(5, store.GetQuantity("sku"));
        Equal(1, store.Snapshot().Count);
        Report(StockImporter.Run(new StringReader("a|sku|2\nb|new|2"), store), 2, 2);
        Equal(7, store.GetQuantity("Sku"));
    }
});
Case("cancel.partial-reader-error-rolls-back", 3, () =>
{
    var store = new StockStore();
    var failure = new IOException("reader broke");
    using var reader = new LineReader("x|SKU|3") { BeforeRead = count => { if (count == 2) throw failure; } };
    Equal(true, ReferenceEquals(failure, Throws<IOException>(() => StockImporter.Run(reader, store))));
    Equal(0, store.Snapshot().Count);
    Report(StockImporter.Run(new StringReader("x|sku|2"), store), 1, 1);
    Equal("sku", store.Snapshot().Keys.Single());
});
#endif

File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

void Case(string id, int introduced, Action action)
{
    try { action(); results.Add(new(id, introduced, true, null)); }
    catch (Exception error) { results.Add(new(id, introduced, false, error.GetType().Name + ": " + error.Message)); }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

static TException Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException error) { return error; }
    throw new Exception("Expected " + typeof(TException).Name + ".");
}

#if STAGE1
static void Report(ImportResult report, int accepted, int applied, params (int LineNumber, string Code)[] issues)
{
    Equal(accepted, report.AcceptedRows);
    Equal(applied, report.AppliedRows);
    Equal(string.Join(";", issues), string.Join(";", report.Issues.Select(issue => (issue.LineNumber, issue.Code))));
}
#endif

sealed record CaseResult(string Id, int Introduced, bool Passed, string? Detail);

sealed class LineReader(params string[] lines) : TextReader
{
    public int ReadCount { get; private set; }
    public bool Disposed { get; private set; }
    public Action<int>? BeforeRead { get; init; }

    public override string? ReadLine()
    {
        ReadCount++;
        BeforeRead?.Invoke(ReadCount);
        return ReadCount <= lines.Length ? lines[ReadCount - 1] : null;
    }

    public override string ReadToEnd() => throw new InvalidOperationException("Expected streaming ReadLine calls.");
    protected override void Dispose(bool disposing) => Disposed = true;
}
