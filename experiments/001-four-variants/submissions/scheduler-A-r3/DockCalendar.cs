namespace DockScheduling;

public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

public sealed record DockBlackout(string? Dock, int StartMinute, int EndMinute);

public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);
    private readonly int? sharedCapacity;
    private readonly DockBlackout[] blackouts = [];

    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities)
    {
        ArgumentNullException.ThrowIfNull(dockCapacities);
        this.dockCapacities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (dock, capacity) in dockCapacities)
        {
            if (string.IsNullOrWhiteSpace(dock) || capacity < 1)
            {
                throw new ArgumentException("Each dock needs a name and positive capacity.", nameof(dockCapacities));
            }
            this.dockCapacities.Add(dock, capacity);
        }
    }

    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int sharedCapacity, IReadOnlyList<DockBlackout> blackouts)
        : this(dockCapacities)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharedCapacity);
        ArgumentNullException.ThrowIfNull(blackouts);

        var blackoutSnapshot = blackouts.ToArray();
        foreach (var blackout in blackoutSnapshot)
        {
            if (blackout is null ||
                (blackout.Dock is not null && !this.dockCapacities.ContainsKey(blackout.Dock)) ||
                blackout.StartMinute < 0 || blackout.EndMinute > 1440 || blackout.StartMinute >= blackout.EndMinute)
            {
                throw new ArgumentException("Invalid blackout.", nameof(blackouts));
            }
        }

        this.sharedCapacity = sharedCapacity;
        this.blackouts = blackoutSnapshot;
    }

    public bool TryReserve(DockBooking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (string.IsNullOrWhiteSpace(booking.Id) || string.IsNullOrWhiteSpace(booking.Dock) ||
            !dockCapacities.TryGetValue(booking.Dock, out int dockCapacity) ||
            booking.StartMinute < 0 || booking.EndMinute > 1440 || booking.EndMinute <= booking.StartMinute || booking.Units < 1)
        {
            throw new ArgumentException("Invalid booking.", nameof(booking));
        }

        if (bookings.ContainsKey(booking.Id) || booking.Units > dockCapacity)
        {
            return false;
        }

        var availability = GetAvailability(booking.Dock, booking.Units, booking.StartMinute, booking.EndMinute);
        for (int minute = booking.StartMinute; minute < booking.EndMinute; minute++)
        {
            if (!availability[minute])
            {
                return false;
            }
        }

        bookings.Add(booking.Id, booking);
        return true;
    }

    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return bookings.Remove(id);
    }

    internal bool TryGetDockCapacity(string dock, out int capacity) => dockCapacities.TryGetValue(dock, out capacity);

    internal bool ContainsBooking(string id) => bookings.ContainsKey(id);

    internal bool[] GetAvailability(string dock, int units, int startMinute, int endMinute)
    {
        var dockChanges = new long[1441];
        var sharedChanges = sharedCapacity.HasValue ? new long[1441] : null;
        var blackoutChanges = new int[1441];

        // Clip endpoint changes to the window so usage is counted at its first minute.
        foreach (var booking in bookings.Values)
        {
            int start = Math.Max(booking.StartMinute, startMinute);
            int end = Math.Min(booking.EndMinute, endMinute);
            if (start < end)
            {
                if (booking.Dock == dock)
                {
                    dockChanges[start] += booking.Units;
                    dockChanges[end] -= booking.Units;
                }
                if (sharedChanges is not null)
                {
                    sharedChanges[start] += booking.Units;
                    sharedChanges[end] -= booking.Units;
                }
            }
        }

        foreach (var blackout in blackouts)
        {
            if (blackout.Dock is null || blackout.Dock == dock)
            {
                int start = Math.Max(blackout.StartMinute, startMinute);
                int end = Math.Min(blackout.EndMinute, endMinute);
                if (start < end)
                {
                    blackoutChanges[start]++;
                    blackoutChanges[end]--;
                }
            }
        }

        var availability = new bool[1440];
        long dockReserved = 0;
        long sharedReserved = 0;
        int activeBlackouts = 0;
        for (int minute = startMinute; minute < endMinute; minute++)
        {
            dockReserved += dockChanges[minute];
            sharedReserved += sharedChanges?[minute] ?? 0;
            activeBlackouts += blackoutChanges[minute];
            availability[minute] = activeBlackouts == 0 &&
                dockReserved + units <= dockCapacities[dock] &&
                (sharedCapacity is null || sharedReserved + units <= sharedCapacity.Value);
        }

        return availability;
    }

    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(booking => booking.Id, StringComparer.Ordinal).ToArray();
}
