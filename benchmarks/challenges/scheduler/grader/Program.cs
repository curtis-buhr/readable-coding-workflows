using System.Text.Json;
using DockScheduling;

var results = new List<CaseResult>();

Case("calendar.capacity-and-half-open-intervals", 0, () =>
{
    var calendar = NewCalendar();
    Equal(true, calendar.TryReserve(new("b", "A", 0, 10, 1)));
    Equal(true, calendar.TryReserve(new("a", "A", 5, 8, 1)));
    Equal(false, calendar.TryReserve(new("c", "A", 7, 9, 1)));
    Equal(true, calendar.TryReserve(new("c", "A", 8, 9, 1)));
    Equal(true, calendar.TryReserve(new("d", "A", 10, 11, 2)));
    Equal(true, calendar.TryReserve(new("e", "B", 0, 10, 2)));
    Equal("a,b,c,d,e", string.Join(",", calendar.Snapshot().Select(booking => booking.Id)));
});
Case("calendar.cancellation-and-snapshot-independence", 0, () =>
{
    var calendar = NewCalendar();
    Equal(true, calendar.TryReserve(new("x", "A", 1439, 1440, 2)));
    var snapshot = calendar.Snapshot();
    Equal(false, calendar.TryReserve(new("x", "B", 0, 10, 1)));
    Equal(false, calendar.Cancel("X"));
    Equal(true, calendar.Cancel("x"));
    Equal(false, calendar.Cancel("x"));
    Equal(1, snapshot.Count);
    Equal(true, calendar.TryReserve(new("x", "A", 1439, 1440, 2)));
});
Case("calendar.rejects-invalid-bookings-before-mutation", 0, () =>
{
    var calendar = NewCalendar();
    Throws<ArgumentNullException>(() => calendar.TryReserve(null!));
    Throws<ArgumentException>(() => calendar.TryReserve(new("x", "a", 0, 1, 1)));
    Throws<ArgumentException>(() => calendar.TryReserve(new("x", "A", 0, 0, 1)));
    Throws<ArgumentException>(() => calendar.TryReserve(new("x", "A", -1, 1, 1)));
    Throws<ArgumentException>(() => calendar.TryReserve(new("x", "A", 1439, 1441, 1)));
    Throws<ArgumentException>(() => calendar.TryReserve(new("x", "A", 0, 1, 0)));
    Equal(0, calendar.Snapshot().Count);
});
Case("calendar.independent-capacity-does-not-overflow", 0, () =>
{
    var calendar = new DockCalendar(new Dictionary<string, int> { ["A"] = int.MaxValue, ["B"] = int.MaxValue });
    Equal(true, calendar.TryReserve(new("a", "A", 5, 8, int.MaxValue)));
    Equal(true, calendar.TryReserve(new("b", "B", 5, 8, int.MaxValue)));
    Equal(false, calendar.TryReserve(new("c", "A", 6, 7, 1)));
});

#if STAGE1
Case("dispatch.earliest-contiguous-fit", 1, () =>
{
    var calendar = NewCalendar();
    Equal(true, calendar.TryReserve(new("busy0", "A", 5, 6, 2)));
    Equal(true, calendar.TryReserve(new("busy1", "A", 9, 11, 2)));
    var dispatcher = new DockDispatcher(calendar);
    Equal(new DockBooking("x", "A", 11, 15, 2), dispatcher.Dispatch(new("x", "A", 2, 15, 4, 2)));
    Equal(3, calendar.Snapshot().Count);
});
Case("dispatch.respects-latest-end-and-large-duration", 1, () =>
{
    var calendar = NewCalendar();
    var dispatcher = new DockDispatcher(calendar);
    Equal<DockBooking?>(null, dispatcher.Dispatch(new("x", "A", 1, 5, int.MaxValue, 1)));
    Equal<DockBooking?>(null, dispatcher.Dispatch(new("x", "A", 1, 5, 1, 3)));
    Equal(0, calendar.Snapshot().Count);
    Equal(new DockBooking("x", "A", 1439, 1440, 2), dispatcher.Dispatch(new("x", "A", 1439, 1440, 1, 2)));
    Equal<DockBooking?>(null, dispatcher.Dispatch(new("x", "B", 1, 5, 1, 1)));
});
Case("dispatch.validation-and-cancellation-integration", 1, () =>
{
    var calendar = NewCalendar();
    var dispatcher = new DockDispatcher(calendar);
    Throws<ArgumentNullException>(() => new DockDispatcher(null!));
    Throws<ArgumentNullException>(() => dispatcher.Dispatch(null!));
    Throws<ArgumentException>(() => dispatcher.Dispatch(new("x", "missing", 0, 1, 2, 2)));
    Throws<ArgumentException>(() => dispatcher.Dispatch(new("x", "A", 1, 1, 2, 2)));
    Throws<ArgumentException>(() => dispatcher.Dispatch(new("x", "A", 0, 2, 0, 2)));
    Equal(new DockBooking("x", "A", 0, 2, 2), dispatcher.Dispatch(new("x", "A", 0, 2, 2, 2)));
    Equal<DockBooking?>(null, dispatcher.Dispatch(new("y", "A", 0, 2, 2, 2)));
    Equal(true, calendar.Cancel("x"));
    Equal(new DockBooking("y", "A", 0, 2, 2), dispatcher.Dispatch(new("y", "A", 0, 2, 2, 2)));
});
#endif

#if STAGE2
Case("batch.best-effort-has-positional-results", 2, () =>
{
    var calendar = NewCalendar();
    var dispatcher = new DockDispatcher(calendar);
    var batch = new DispatchRequest[] { new("x", "A", 0, 4, 3, 2), new("y", "A", 0, 4, 2, 2), new("z", "A", 0, 4, 1, 2), new("x", "B", 0, 4, 1, 2) };
    var results = dispatcher.DispatchBatch(batch, DispatchPolicy.BestEffort);
    Equal(4, results.Count);
    Equal(new DockBooking("x", "A", 0, 3, 2), results[0]);
    Equal<DockBooking?>(null, results[1]);
    Equal(new DockBooking("z", "A", 3, 4, 2), results[2]);
    Equal<DockBooking?>(null, results[3]);
    Equal("x,z", string.Join(",", calendar.Snapshot().Select(booking => booking.Id)));
});
Case("batch.strict-rollback-keeps-preexisting-bookings", 2, () =>
{
    var calendar = NewCalendar();
    Equal(true, calendar.TryReserve(new("existing", "A", 4, 5, 2)));
    var dispatcher = new DockDispatcher(calendar);
    var batch = new DispatchRequest[] { new("x", "A", 0, 4, 2, 2), new("existing", "B", 0, 4, 1, 2), new("z", "B", 0, 4, 1, 2) };
    var results = dispatcher.DispatchBatch(batch, DispatchPolicy.AllOrNothing);
    Equal(3, results.Count);
    Equal(true, results.All(result => result is null));
    Equal("existing", calendar.Snapshot().Single().Id);
    Equal(true, calendar.TryReserve(new("x", "A", 0, 4, 2)));
});
Case("batch.strict-success-and-empty-batches", 2, () =>
{
    var calendar = NewCalendar();
    var dispatcher = new DockDispatcher(calendar);
    foreach (var mode in Enum.GetValues<DispatchPolicy>())
        Equal(0, dispatcher.DispatchBatch(Array.Empty<DispatchRequest>(), mode).Count);
    var batch = new DispatchRequest[] { new("x", "A", 0, 5, 2, 2), new("y", "A", 0, 5, 2, 2) };
    var results = dispatcher.DispatchBatch(batch, DispatchPolicy.AllOrNothing);
    Equal(new DockBooking("x", "A", 0, 2, 2), results[0]);
    Equal(new DockBooking("y", "A", 2, 4, 2), results[1]);
    Equal(2, calendar.Snapshot().Count);
});
Case("batch.validates-all-input-before-mutating", 2, () =>
{
    foreach (var mode in Enum.GetValues<DispatchPolicy>())
    {
        var calendar = NewCalendar();
        var dispatcher = new DockDispatcher(calendar);
        var first = new DispatchRequest("a", "A", 0, 2, 2, 2);
        var impossible = new DispatchRequest("b", "A", 0, 2, 3, 2);
        Throws<ArgumentNullException>(() => dispatcher.DispatchBatch(null!, mode));
        Throws<ArgumentException>(() => dispatcher.DispatchBatch(new[] { first, impossible, null! }, mode));
        Throws<ArgumentException>(() => dispatcher.DispatchBatch(new[] { first, impossible, new DispatchRequest("x", "missing", 0, 1, 1, 1) }, mode));
        Equal(0, calendar.Snapshot().Count);
    }
    Throws<ArgumentOutOfRangeException>(() => new DockDispatcher(NewCalendar()).DispatchBatch(Array.Empty<DispatchRequest>(), (DispatchPolicy)99));
});
Case("batch.rejected-id-can-be-used-by-later-best-effort-entry", 2, () =>
{
    var calendar = NewCalendar();
    var dispatcher = new DockDispatcher(calendar);
    var results = dispatcher.DispatchBatch(new DispatchRequest[] { new("x", "A", 0, 1, 2, 2), new("x", "A", 0, 1, 1, 2) }, DispatchPolicy.BestEffort);
    Equal<DockBooking?>(null, results[0]);
    Equal(new DockBooking("x", "A", 0, 1, 2), results[1]);
});
#endif

#if STAGE3
Case("shared.direct-reservation-enforces-both-capacities", 3, () =>
{
    var calendar = new DockCalendar(Capacities(), 3, Array.Empty<DockBlackout>());
    Equal(true, calendar.TryReserve(new("a", "A", 0, 10, 2)));
    Equal(false, calendar.TryReserve(new("b", "B", 0, 10, 2)));
    Equal(true, calendar.TryReserve(new("b", "B", 0, 10, 1)));
    Equal(false, calendar.TryReserve(new("x", "A", 10, 11, 3)));
    Equal(true, calendar.TryReserve(new("y", "A", 10, 11, 2)));
    Equal(true, calendar.Cancel("a"));
    Equal(true, calendar.TryReserve(new("z", "B", 0, 10, 1)));
});
Case("blackouts.scopes-and-half-open-boundaries", 3, () =>
{
    var calendar = new DockCalendar(Capacities(), 4, new DockBlackout[] { new(null, 9, 11), new("A", 4, 7), new("A", 6, 8) });
    Equal(true, calendar.TryReserve(new("a", "A", 2, 4, 2)));
    Equal(false, calendar.TryReserve(new("b", "A", 3, 5, 1)));
    Equal(true, calendar.TryReserve(new("b", "B", 3, 5, 1)));
    Equal(true, calendar.TryReserve(new("c", "A", 8, 9, 2)));
    Equal(false, calendar.TryReserve(new("d", "B", 10, 12, 1)));
    Equal(true, calendar.TryReserve(new("d", "B", 11, 12, 1)));
});
Case("dispatch.earliest-fit-combines-shared-limit-and-blackouts", 3, () =>
{
    var calendar = new DockCalendar(Capacities(), 3, new DockBlackout[] { new("B", 7, 8), new(null, 10, 12) });
    Equal(true, calendar.TryReserve(new("a", "A", 0, 6, 2)));
    var dispatcher = new DockDispatcher(calendar);
    Equal(new DockBooking("b", "B", 12, 15, 2), dispatcher.Dispatch(new("b", "B", 0, 15, 3, 2)));
    Equal(true, calendar.Cancel("a"));
    Equal(new DockBooking("c", "B", 0, 3, 2), dispatcher.Dispatch(new("c", "B", 0, 15, 3, 2)));
    Equal<DockBooking?>(null, dispatcher.Dispatch(new("d", "B", 7, 8, 1, 1)));
});
Case("batch.both-policies-use-shared-rules-and-release-rollback", 3, () =>
{
    foreach (var mode in Enum.GetValues<DispatchPolicy>())
    {
        var calendar = new DockCalendar(Capacities(), 2, new DockBlackout[] { new(null, 4, 5) });
        var dispatcher = new DockDispatcher(calendar);
        var batch = new DispatchRequest[] { new("x", "A", 0, 5, 3, 2), new("y", "B", 0, 5, 2, 2), new("z", "B", 0, 5, 1, 2) };
        var results = dispatcher.DispatchBatch(batch, mode);
        Equal(3, results.Count);
        if (mode == DispatchPolicy.BestEffort)
        {
            Equal(new DockBooking("x", "A", 0, 3, 2), results[0]);
            Equal<DockBooking?>(null, results[1]);
            Equal(new DockBooking("z", "B", 3, 4, 2), results[2]);
        }
        else
        {
            Equal(true, results.All(result => result is null));
            Equal(0, calendar.Snapshot().Count);
            Equal(new DockBooking("q", "B", 0, 4, 2), dispatcher.Dispatch(new("q", "B", 0, 5, 4, 2)));
        }
    }
});
Case("configuration.validation-and-independence", 3, () =>
{
    var capacities = Capacities();
    Throws<ArgumentOutOfRangeException>(() => new DockCalendar(capacities, 0, Array.Empty<DockBlackout>()));
    Throws<ArgumentNullException>(() => new DockCalendar(capacities, 1, null!));
    Throws<ArgumentException>(() => new DockCalendar(capacities, 1, new DockBlackout[] { null! }));
    Throws<ArgumentException>(() => new DockCalendar(capacities, 1, new DockBlackout[] { new("a", 1, 2) }));
    Throws<ArgumentException>(() => new DockCalendar(capacities, 1, new DockBlackout[] { new(null, 1, 1) }));
    var blackouts = new List<DockBlackout> { new("A", 0, 3) };
    var calendar = new DockCalendar(capacities, 3, blackouts);
    capacities["A"] = 10;
    blackouts.Clear();
    Equal(false, calendar.TryReserve(new("x", "A", 0, 2, 2)));
    Equal(false, calendar.TryReserve(new("x", "A", 3, 4, 3)));
    Equal(true, calendar.TryReserve(new("x", "A", 3, 4, 2)));
});
Case("shared.large-capacity-does-not-wrap", 3, () =>
{
    var calendar = new DockCalendar(new Dictionary<string, int> { ["A"] = int.MaxValue, ["B"] = int.MaxValue }, int.MaxValue, Array.Empty<DockBlackout>());
    Equal(true, calendar.TryReserve(new("x", "A", 0, 1, int.MaxValue)));
    Equal(false, calendar.TryReserve(new("y", "B", 0, 1, int.MaxValue)));
    Equal(true, calendar.TryReserve(new("y", "B", 1, 2, int.MaxValue)));
});
#endif

File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

void Case(string id, int introduced, Action action)
{
    try { action(); results.Add(new(id, introduced, true, null)); }
    catch (Exception error) { results.Add(new(id, introduced, false, error.GetType().Name + ": " + error.Message)); }
}

static Dictionary<string, int> Capacities() => new() { ["A"] = 2, ["B"] = 2 };
static DockCalendar NewCalendar() => new(Capacities());

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception("Expected " + typeof(TException).Name + ".");
}

sealed record CaseResult(string Id, int Introduced, bool Passed, string? Detail);
