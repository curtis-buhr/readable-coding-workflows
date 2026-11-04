namespace DockScheduling;

public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

/// <summary>Blocks [StartMinute, EndMinute) on the specified dock, or all docks when Dock is null.</summary>
public sealed record DockBlackout(string? Dock, int StartMinute, int EndMinute);

public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly int? sharedCapacity;
    private readonly DockBlackout[] blackouts;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);

    /// <summary>Creates a calendar with independent dock capacities and no blackouts or shared limit.</summary>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities)
        : this(dockCapacities, null, Array.Empty<DockBlackout>())
    {
    }

    /// <summary>Creates a calendar from a snapshot of dock capacities and blackouts, with an additional shared limit.</summary>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int sharedCapacity, IReadOnlyList<DockBlackout> blackouts)
        : this(dockCapacities, (int?)sharedCapacity, blackouts)
    {
    }

    private DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int? sharedCapacity, IReadOnlyList<DockBlackout> blackouts)
    {
        ArgumentNullException.ThrowIfNull(dockCapacities);
        if (sharedCapacity is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sharedCapacity), sharedCapacity, "Shared capacity must be positive.");
        }
        ArgumentNullException.ThrowIfNull(blackouts);

        this.dockCapacities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (dock, capacity) in dockCapacities)
        {
            if (string.IsNullOrWhiteSpace(dock) || capacity < 1)
            {
                throw new ArgumentException("Each dock needs a name and positive capacity.", nameof(dockCapacities));
            }
            this.dockCapacities.Add(dock, capacity);
        }

        this.sharedCapacity = sharedCapacity;
        this.blackouts = blackouts.ToArray();
        foreach (var blackout in this.blackouts)
        {
            if (blackout is null ||
                (blackout.Dock is not null && !this.dockCapacities.ContainsKey(blackout.Dock)) ||
                blackout.StartMinute < 0 || blackout.EndMinute > 1440 || blackout.StartMinute >= blackout.EndMinute)
            {
                throw new ArgumentException("Invalid blackout.", nameof(blackouts));
            }
        }
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

        if (bookings.ContainsKey(booking.Id) || booking.Units > dockCapacity ||
            FindEarliestStart(booking.Dock, booking.StartMinute, booking.EndMinute,
                booking.EndMinute - booking.StartMinute, booking.Units) is null)
        {
            return false;
        }

        bookings.Add(booking.Id, booking);
        return true;
    }

    internal int? FindEarliestStart(string dock, int earliestStartMinute, int latestEndMinute, int durationMinutes, int units)
    {
        int dockCapacity = dockCapacities[dock];
        if (durationMinutes > latestEndMinute - earliestStartMinute || units > dockCapacity ||
            (sharedCapacity.HasValue && units > sharedCapacity.Value))
        {
            return null;
        }

        var dockChanges = new long[1441];
        var sharedChanges = sharedCapacity.HasValue ? new long[1441] : null;
        foreach (var booking in bookings.Values)
        {
            bool sameDock = string.Equals(booking.Dock, dock, StringComparison.Ordinal);
            if (!sameDock && sharedChanges is null)
            {
                continue;
            }

            int start = Math.Max(booking.StartMinute, earliestStartMinute);
            int end = Math.Min(booking.EndMinute, latestEndMinute);
            if (start < end)
            {
                if (sameDock)
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

        var blackoutChanges = new int[1441];
        foreach (var blackout in blackouts)
        {
            if (blackout.Dock is not null && !string.Equals(blackout.Dock, dock, StringComparison.Ordinal))
            {
                continue;
            }

            int start = Math.Max(blackout.StartMinute, earliestStartMinute);
            int end = Math.Min(blackout.EndMinute, latestEndMinute);
            if (start < end)
            {
                blackoutChanges[start]++;
                blackoutChanges[end]--;
            }
        }

        long dockReserved = 0;
        long sharedReserved = 0;
        int activeBlackouts = 0;
        int availableMinutes = 0;
        for (int minute = earliestStartMinute; minute < latestEndMinute; minute++)
        {
            dockReserved += dockChanges[minute];
            sharedReserved += sharedChanges?[minute] ?? 0;
            activeBlackouts += blackoutChanges[minute];
            if (activeBlackouts > 0 || dockReserved + units > dockCapacity ||
                (sharedCapacity.HasValue && sharedReserved + units > sharedCapacity.Value))
            {
                availableMinutes = 0;
                continue;
            }

            availableMinutes++;
            if (availableMinutes == durationMinutes)
            {
                return minute + 1 - durationMinutes;
            }
        }

        return null;
    }

    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return bookings.Remove(id);
    }

    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(booking => booking.Id, StringComparer.Ordinal).ToArray();

    internal bool TryGetDockCapacity(string dock, out int capacity) => dockCapacities.TryGetValue(dock, out capacity);
}
