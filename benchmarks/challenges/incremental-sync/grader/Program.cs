using System.Text.Json;
using IncrementalSync;

var results = new List<CaseResult>();

await Check("single.stops-at-first-decline", 0, async () =>
{
    var sink = new RecordingSink { Accept = (_, record, _) => record.Sequence != 4 };
    var engine = new SyncEngine(sink);
    Equal(1, await engine.SyncAsync("north", Records(2, 4, 8)));
    Equal("north:2,north:4", sink.AttemptTrace);
});

await Check("single.validates-entire-input-before-writing", 0, async () =>
{
    var sink = new RecordingSink();
    var engine = new SyncEngine(sink);
    await Throws<ArgumentException>(async () => await engine.SyncAsync(" ", Records(1)));
    await Throws<ArgumentNullException>(async () => await engine.SyncAsync("north", null!));
    await Throws<ArgumentException>(async () => await engine.SyncAsync("north", Records(2, 1)));
    await Throws<ArgumentException>(async () => await engine.SyncAsync("north", Records(2, 2)));
    await Throws<ArgumentException>(async () => await engine.SyncAsync("north", Records(0)));
    await Throws<ArgumentException>(async () => await engine.SyncAsync("north", [new(1, "ok"), null!]));
    await Throws<ArgumentException>(async () => await engine.SyncAsync("north", [new(1, null!)]));
    Equal("", sink.AttemptTrace);
});

await Check("single.empty-batch", 0, async () =>
{
    var sink = new RecordingSink();
    Equal(0, await new SyncEngine(sink).SyncAsync("north", []));
    Equal("", sink.AttemptTrace);
});

await Check("single.passes-token-and-stops-on-sink-error", 0, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var failure = new InvalidOperationException("sink failed");
    var sink = new RecordingSink
    {
        Accept = (_, record, token) =>
        {
            Equal(cancellation.Token, token);
            return record.Sequence == 1 ? true : throw failure;
        }
    };
    var observed = await Throws<InvalidOperationException>(async () =>
        await new SyncEngine(sink).SyncAsync("north", Records(1, 2, 3), cancellation.Token));
    Equal(true, ReferenceEquals(failure, observed));
    Equal("north:1,north:2", sink.AttemptTrace);
});

await Check("single.pre-canceled-call-has-no-writes", 0, async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var sink = new RecordingSink();
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink).SyncAsync("north", Records(1), cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("", sink.AttemptTrace);
});

#if REV1
await Check("checkpoint.resumes-a-fresh-engine-in-source-order", 1, async () =>
{
    var store = new Checkpoints();
    store.Save("north", 2);
    var sink = new RecordingSink
    {
        Accept = (tenant, record, _) =>
        {
            Equal(record.Sequence == 5 ? 2L : 5L, store.Read(tenant));
            return true;
        }
    };
    var engine = new SyncEngine(sink, store);
    Equal(2, await engine.SyncAsync("north", Records(1, 2, 5, 9)));
    Equal(9L, store.Read("north"));
    Equal(0, await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2, 5, 9)));
    Equal("north:5,north:9", sink.AttemptTrace);
    Equal("north:2,north:5,north:9", string.Join(",", store.Saves));
});

await Check("checkpoint.refusal-keeps-record-eligible-for-retry", 1, async () =>
{
    var store = new Checkpoints();
    var sink = new RecordingSink { Accept = (_, record, _) => record.Sequence != 7 };
    Equal(1, await new SyncEngine(sink, store).SyncAsync("north", Records(4, 7, 9)));
    Equal(4L, store.Read("north"));
    sink.Accept = (_, _, _) => true;
    Equal(2, await new SyncEngine(sink, store).SyncAsync("north", Records(4, 7, 9)));
    Equal("north:4,north:7,north:7,north:9", sink.AttemptTrace);
    Equal("north:4,north:7,north:9", string.Join(",", store.Saves));
});

await Check("checkpoint.save-failure-stops-later-writes", 1, async () =>
{
    var failure = new IOException("checkpoint unavailable");
    var store = new Checkpoints { BeforeSave = (_, sequence) => { if (sequence == 2) throw failure; } };
    var sink = new RecordingSink();
    var observed = await Throws<IOException>(async () =>
        await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2, 3)));
    Equal(true, ReferenceEquals(failure, observed));
    Equal(1L, store.Read("north"));
    Equal("north:1,north:2", sink.AttemptTrace);
});

await Check("checkpoint.validation-includes-already-saved-records", 1, async () =>
{
    var store = new Checkpoints();
    store.Save("north", 8);
    var sink = new RecordingSink();
    await Throws<ArgumentException>(async () =>
        await new SyncEngine(sink, store).SyncAsync("north", Records(2, 1, 9)));
    Equal("", sink.AttemptTrace);
    Equal("north:8", string.Join(",", store.Saves));
});

await Check("checkpoint.compatibility-constructor-is-instance-and-tenant-scoped", 1, async () =>
{
    var sink = new RecordingSink();
    var engine = new SyncEngine(sink);
    Equal(1, await engine.SyncAsync("North", Records(1)));
    Equal(0, await engine.SyncAsync("North", Records(1)));
    Equal(1, await engine.SyncAsync("north", Records(1)));
    Equal(1, await new SyncEngine(sink).SyncAsync("North", Records(1)));
    Equal("North:1,north:1,North:1", sink.AttemptTrace);
    await Throws<ArgumentNullException>(() => { _ = new SyncEngine(null!); return Task.CompletedTask; });
    await Throws<ArgumentNullException>(() => { _ = new SyncEngine(sink, null!); return Task.CompletedTask; });
});
#endif

#if REV2
await Check("batch.isolates-false-and-retryable-sink-failures", 2, async () =>
{
    var sink = new RecordingSink
    {
        Accept = (tenant, record, _) =>
            (tenant, record.Sequence) switch
            {
                ("north", 2) => throw new RetryableSinkException("retry north"),
                ("south", 1) => false,
                _ => true
            }
    };
    var store = new Checkpoints();
    TenantBatch[] batches = [new("north", Records(1, 2, 3)), new("south", Records(1, 2)),
        new("North", Records(1)), new("empty", [])];
    var progress = await new SyncEngine(sink, store).SyncAllAsync(batches);
    Equal("north:1:True,south:0:True,North:1:False,empty:0:False", ProgressTrace(progress));
    Equal("north:1,north:2,south:1,North:1", sink.AttemptTrace);
    Equal("north:1,North:1", string.Join(",", store.Saves));

    // A fresh engine should retry only the records not yet acknowledged for each tenant.
    sink.Attempts.Clear();
    sink.Accept = (_, _, _) => true;
    progress = await new SyncEngine(sink, store).SyncAllAsync(batches);
    Equal("north:2:False,south:2:False,North:0:False,empty:0:False", ProgressTrace(progress));
    Equal("north:2,north:3,south:1,south:2", sink.AttemptTrace);
});

await Check("batch.rejects-malformed-or-duplicate-later-tenant-before-effects", 2, async () =>
{
    var sink = new RecordingSink();
    var store = new Checkpoints();
    var engine = new SyncEngine(sink, store);
    await Throws<ArgumentNullException>(async () => await engine.SyncAllAsync(null!));
    await Throws<ArgumentException>(async () =>
        await engine.SyncAllAsync([new("north", Records(1)), new("north", Records(2))]));
    await Throws<ArgumentException>(async () =>
        await engine.SyncAllAsync([new("north", Records(1)), new("south", Records(2, 1))]));
    await Throws<ArgumentException>(async () =>
        await engine.SyncAllAsync([new("north", Records(1)), null!]));
    Equal("", sink.AttemptTrace);
    Equal(0, store.Saves.Count);
});

await Check("batch.retryable-checkpoint-errors-are-not-sink-errors", 2, async () =>
{
    var failure = new RetryableSinkException("checkpoint save failed");
    var store = new Checkpoints { BeforeSave = (_, _) => throw failure };
    var sink = new RecordingSink();
    var observed = await Throws<RetryableSinkException>(async () =>
        await new SyncEngine(sink, store).SyncAllAsync([new("north", Records(1)), new("south", Records(1))]));
    Equal(true, ReferenceEquals(failure, observed));
    Equal("north:1", sink.AttemptTrace);
    Equal(0, store.Saves.Count);
});

await Check("batch.fatal-sink-failure-stops-later-tenants", 2, async () =>
{
    var failure = new IOException("sink failed");
    var sink = new RecordingSink { Accept = (_, record, _) => record.Sequence == 1 ? true : throw failure };
    var store = new Checkpoints();
    var observed = await Throws<IOException>(async () =>
        await new SyncEngine(sink, store).SyncAllAsync([new("north", Records(1, 2)), new("south", Records(1))]));
    Equal(true, ReferenceEquals(failure, observed));
    Equal("north:1,north:2", sink.AttemptTrace);
    Equal(1L, store.Read("north"));
});

await Check("batch.single-tenant-api-still-propagates-retryable-sink-error", 2, async () =>
{
    var failure = new RetryableSinkException("try again");
    var sink = new RecordingSink { Accept = (_, record, _) => record.Sequence == 1 ? true : throw failure };
    var store = new Checkpoints();
    var observed = await Throws<RetryableSinkException>(async () =>
        await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2, 3)));
    Equal(true, ReferenceEquals(failure, observed));
    Equal("north:1,north:2", sink.AttemptTrace);
    Equal(1L, store.Read("north"));
});

await Check("batch.empty-valid-input-observes-cancellation", 2, async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var sink = new RecordingSink();
    var engine = new SyncEngine(sink);
    Equal(0, (await engine.SyncAllAsync([])).Count);
    var observed = await Throws<OperationCanceledException>(async () =>
        await engine.SyncAllAsync([], cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("", sink.AttemptTrace);
});
#endif

#if REV3
await Check("cancel.late-acknowledgement-replays-without-duplicate-effects", 3, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var store = new Checkpoints();
    var sink = new RecordingSink();
    sink.Accept = (tenant, record, _) =>
    {
        sink.ApplyDurably(tenant, record);
        if (record.Sequence == 2)
            cancellation.Cancel();
        return true;
    };
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2, 3), cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("north:1,north:2", sink.AttemptTrace);
    Equal("north:1", string.Join(",", store.Saves));
    Equal(2, sink.Effects.Count);

    // The retry sends the original record again while the durable sink effect stays unique.
    sink.Accept = (tenant, record, _) => { sink.ApplyDurably(tenant, record); return true; };
    Equal(2, await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2, 3)));
    Equal(1, await new SyncEngine(sink, store).SyncAsync("south", Records(2)));
    Equal("north:1,north:2,north:2,north:3,south:2", sink.AttemptTrace);
    Equal("north:1,north:2,north:3,south:2", string.Join(",", store.Saves));
    Equal(4, sink.Effects.Count);
});

await Check("cancel.asynchronous-late-acknowledgement-stops-batch", 3, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new Checkpoints();
    var sink = new RecordingSink
    {
        AcceptAsync = (tenant, _, _) => tenant == "north"
            ? ValueTask.FromResult(true)
            : new ValueTask<bool>(acknowledgement.Task)
    };
    var pending = new SyncEngine(sink, store).SyncAllAsync(
        [new("north", Records(1)), new("south", Records(1)), new("east", Records(1))], cancellation.Token);
    Equal(false, pending.IsCompleted);
    Equal("north:1", string.Join(",", store.Saves));

    // Complete the sink after cancellation to test the async boundary directly.
    cancellation.Cancel();
    acknowledgement.SetResult(true);
    var observed = await Throws<OperationCanceledException>(async () => await pending);
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("north:1,south:1", sink.AttemptTrace);
    Equal("north:1", string.Join(",", store.Saves));
});

await Check("cancel.decline-after-cancellation-does-not-start-next-tenant", 3, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var store = new Checkpoints();
    var sink = new RecordingSink { Accept = (_, _, _) => { cancellation.Cancel(); return false; } };
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink, store).SyncAllAsync([new("north", Records(1)), new("south", Records(1))],
            cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("north:1", sink.AttemptTrace);
    Equal(0, store.Saves.Count);
});

await Check("cancel.retryable-sink-error-surfaces-the-caller-cancellation", 3, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var store = new Checkpoints();
    var sink = new RecordingSink
    {
        Accept = (_, _, _) => { cancellation.Cancel(); throw new RetryableSinkException("interrupted"); }
    };
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink, store).SyncAllAsync([new("north", Records(1)), new("south", Records(1))],
            cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("north:1", sink.AttemptTrace);
    Equal(0, store.Saves.Count);
});

await Check("cancel.single-api-uses-same-retryable-error-cancellation-rule", 3, async () =>
{
    using var cancellation = new CancellationTokenSource();
    var sink = new RecordingSink
    {
        Accept = (_, _, _) => { cancellation.Cancel(); throw new RetryableSinkException("interrupted"); }
    };
    var store = new Checkpoints();
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink, store).SyncAsync("north", Records(1, 2), cancellation.Token));
    Equal(cancellation.Token, observed.CancellationToken);
    Equal("north:1", sink.AttemptTrace);
    Equal(0, store.Saves.Count);
});

await Check("cancel.sink-thrown-cancellation-retains-earlier-checkpoint", 3, async () =>
{
    var failure = new OperationCanceledException("sink canceled");
    var sink = new RecordingSink { Accept = (_, record, _) => record.Sequence == 1 ? true : throw failure };
    var store = new Checkpoints();
    var observed = await Throws<OperationCanceledException>(async () =>
        await new SyncEngine(sink, store).SyncAllAsync([new("north", Records(1, 2, 3)), new("south", Records(1))]));
    Equal(true, ReferenceEquals(failure, observed));
    Equal("north:1,north:2", sink.AttemptTrace);
    Equal("north:1", string.Join(",", store.Saves));
});
#endif

Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
}));

async Task Check(string id, int introduced, Func<Task> test)
{
    try
    {
        await test();
        results.Add(new CaseResult(id, introduced, true, null));
    }
    catch (Exception error)
    {
        results.Add(new CaseResult(id, introduced, false, $"{error.GetType().Name}: {error.Message}"));
    }
}

static void Equal<T>(T expected, T observed)
{
    if (!EqualityComparer<T>.Default.Equals(expected, observed))
        throw new Exception($"Expected {expected}, received {observed}.");
}

static async Task<TException> Throws<TException>(Func<Task> test) where TException : Exception
{
    try
    {
        await test();
    }
    catch (TException error)
    {
        return error;
    }
    throw new Exception($"Expected {typeof(TException).Name}.");
}

static SourceRecord[] Records(params long[] sequences) =>
    sequences.Select(sequence => new SourceRecord(sequence, $"payload-{sequence}")).ToArray();

#if REV2
static string ProgressTrace(IReadOnlyList<TenantProgress> progress) =>
    string.Join(",", progress.Select(item => $"{item.TenantId}:{item.AcknowledgedCount}:{item.Blocked}"));
#endif

sealed record CaseResult(string Id, int Introduced, bool Passed, string? Detail);

sealed class RecordingSink : IRecordSink
{
    public List<(string Tenant, SourceRecord Record)> Attempts { get; } = [];
    public Dictionary<(string Tenant, long Sequence), string> Effects { get; } = [];
    public Func<string, SourceRecord, CancellationToken, bool> Accept { get; set; } = (_, _, _) => true;
    public Func<string, SourceRecord, CancellationToken, ValueTask<bool>>? AcceptAsync { get; init; }
    public string AttemptTrace => string.Join(",", Attempts.Select(item => $"{item.Tenant}:{item.Record.Sequence}"));

    public ValueTask<bool> WriteAsync(string tenantId, SourceRecord record, CancellationToken cancellationToken)
    {
        Attempts.Add((tenantId, record));
        return AcceptAsync is null
            ? ValueTask.FromResult(Accept(tenantId, record, cancellationToken))
            : AcceptAsync(tenantId, record, cancellationToken);
    }

    public void ApplyDurably(string tenantId, SourceRecord record)
    {
        var identity = (tenantId, record.Sequence);
        if (record.Payload != $"payload-{record.Sequence}" ||
            (Effects.TryGetValue(identity, out var saved) && saved != record.Payload))
            throw new InvalidOperationException("The record payload changed during sync.");
        Effects.TryAdd(identity, record.Payload);
    }
}

#if REV1
sealed class Checkpoints : ICheckpointStore
{
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    public List<string> Saves { get; } = [];
    public Action<string, long>? BeforeSave { get; init; }

    public long Read(string tenantId) => _sequences.GetValueOrDefault(tenantId);

    public void Save(string tenantId, long sequence)
    {
        BeforeSave?.Invoke(tenantId, sequence);
        if (sequence <= Read(tenantId))
            throw new InvalidOperationException("A checkpoint must advance strictly.");
        _sequences[tenantId] = sequence;
        Saves.Add($"{tenantId}:{sequence}");
    }
}
#endif
