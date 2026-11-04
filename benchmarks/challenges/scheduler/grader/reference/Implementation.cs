namespace DockScheduling;

public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);
#if STAGE3
public sealed record DockBlackout(string? Dock, int StartMinute, int EndMinute);
#endif

public sealed class DockCalendar
{
    private readonly Dictionary<string, int> capacities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);
#if STAGE3
    private readonly int? sharedCapacity;
    private readonly DockBlackout[] blackouts = [];
#endif

    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities)
    {
        ArgumentNullException.ThrowIfNull(dockCapacities);
        foreach (var (dock, capacity) in dockCapacities)
        {
            if (string.IsNullOrWhiteSpace(dock) || capacity < 1) throw new ArgumentException(nameof(dockCapacities));
            capacities.Add(dock, capacity);
        }
    }

#if STAGE3
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int sharedCapacity, IReadOnlyList<DockBlackout> blackouts)
        : this(dockCapacities)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharedCapacity);
        ArgumentNullException.ThrowIfNull(blackouts);
        if (blackouts.Any(item => item is null || item.Dock is not null && !KnowsDock(item.Dock) || !ValidInterval(item.StartMinute, item.EndMinute)))
            throw new ArgumentException(nameof(blackouts));
        this.sharedCapacity = sharedCapacity;
        this.blackouts = blackouts.ToArray();
    }
#endif

    internal bool KnowsDock(string dock) => capacities.ContainsKey(dock);
    internal static bool ValidInterval(int start, int end) => start >= 0 && start < end && end <= 1440;

    public bool TryReserve(DockBooking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (string.IsNullOrWhiteSpace(booking.Id) || string.IsNullOrWhiteSpace(booking.Dock) ||
            !capacities.TryGetValue(booking.Dock, out int capacity) || !ValidInterval(booking.StartMinute, booking.EndMinute) || booking.Units < 1)
            throw new ArgumentException(nameof(booking));
        if (bookings.ContainsKey(booking.Id)) return false;
#if STAGE3
        if (blackouts.Any(item => (item.Dock is null || item.Dock == booking.Dock) && item.StartMinute < booking.EndMinute && item.EndMinute > booking.StartMinute))
            return false;
#endif
        var overlapping = bookings.Values.Where(item => item.StartMinute < booking.EndMinute && item.EndMinute > booking.StartMinute).ToArray();
        for (int minute = booking.StartMinute; minute < booking.EndMinute; minute++)
        {
            long dockUsage = booking.Units;
            long totalUsage = booking.Units;
            foreach (var item in overlapping)
            {
                if (minute < item.StartMinute || minute >= item.EndMinute) continue;
                totalUsage += item.Units;
                if (item.Dock == booking.Dock) dockUsage += item.Units;
            }
            if (dockUsage > capacity) return false;
#if STAGE3
            if (sharedCapacity is int limit && totalUsage > limit) return false;
#endif
        }
        bookings.Add(booking.Id, booking);
        return true;
    }

    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return bookings.Remove(id);
    }

    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
}

#if STAGE1
public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);
#endif
#if STAGE2
public enum DispatchPolicy { BestEffort, AllOrNothing }
#endif

#if STAGE1
public sealed class DockDispatcher
{
    private readonly DockCalendar calendar;

    public DockDispatcher(DockCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        this.calendar = calendar;
    }

    public DockBooking? Dispatch(DispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return Place(request);
    }

    private void Validate(DispatchRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.KnowsDock(request.Dock) || !DockCalendar.ValidInterval(request.EarliestStartMinute, request.LatestEndMinute) ||
            request.DurationMinutes < 1 || request.Units < 1)
            throw new ArgumentException(nameof(request));
    }

    private DockBooking? Place(DispatchRequest request)
    {
        int lastStart = request.LatestEndMinute - request.DurationMinutes;
        for (int start = request.EarliestStartMinute; start <= lastStart; start++)
        {
            var booking = new DockBooking(request.Id, request.Dock, start, start + request.DurationMinutes, request.Units);
            if (calendar.TryReserve(booking)) return booking;
        }
        return null;
    }

#if STAGE2
    public IReadOnlyList<DockBooking?> DispatchBatch(IReadOnlyList<DispatchRequest> requests, DispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var copy = requests.ToArray();
        foreach (var request in copy) Validate(request);

        var results = new DockBooking?[copy.Length];
        for (int index = 0; index < copy.Length; index++)
        {
            var result = Place(copy[index]);
            if (result is null && policy == DispatchPolicy.AllOrNothing)
            {
                foreach (var prior in results.Take(index)) calendar.Cancel(prior!.Id);
                return new DockBooking?[copy.Length];
            }
            results[index] = result;
        }
        return results;
    }
#endif
}
#endif
