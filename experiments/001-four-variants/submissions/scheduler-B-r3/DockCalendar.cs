namespace DockScheduling;

/// <summary>Describes capacity units to reserve on a named dock over a half-open interval.</summary>
/// <param name="Id">The case-sensitive booking identifier.</param>
/// <param name="Dock">The case-sensitive dock name.</param>
/// <param name="StartMinute">The inclusive start minute, measured from midnight.</param>
/// <param name="EndMinute">The exclusive end minute, measured from midnight.</param>
/// <param name="Units">The capacity units reserved at every minute of the interval.</param>
public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

/// <summary>Describes a half-open interval during which reservations are blocked on one or all docks.</summary>
/// <param name="Dock">The ordinal, case-sensitive dock name, or null to block all docks.</param>
/// <param name="StartMinute">The inclusive start minute, measured from midnight.</param>
/// <param name="EndMinute">The exclusive end minute, measured from midnight.</param>
public sealed record DockBlackout(string? Dock, int StartMinute, int EndMinute);

/// <summary>Maintains one day's bookings, capacity limits, and scheduled blackouts for sequential use.</summary>
public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);
    private readonly int? sharedCapacity;
    private readonly DockBlackout[] blackouts = Array.Empty<DockBlackout>();

    /// <summary>Copies independent dock capacities with ordinal names, no shared limit, and no blackouts.</summary>
    /// <param name="dockCapacities">Dock names and their positive capacity units.</param>
    /// <exception cref="ArgumentNullException">The capacity dictionary is null.</exception>
    /// <exception cref="ArgumentException">A dock name is blank or a capacity is not positive.</exception>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities)
    {
        ArgumentNullException.ThrowIfNull(dockCapacities);

        // Own the capacity configuration so later changes to the caller's dictionary cannot affect bookings.
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

    /// <summary>Copies dock capacities and blackouts, and limits simultaneous units across all docks.</summary>
    /// <param name="dockCapacities">Ordinal, case-sensitive dock names and their positive capacity units.</param>
    /// <param name="sharedCapacity">The positive maximum units reserved across all docks at any minute.</param>
    /// <param name="blackouts">Intervals within [0, 1440] with start before end; a null dock blocks all docks.</param>
    /// <exception cref="ArgumentNullException">The capacity dictionary or blackout collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The shared capacity is not positive.</exception>
    /// <exception cref="ArgumentException">A dock capacity is invalid, or a blackout is null, malformed, or names an unknown dock.</exception>
    /// <remarks>Later changes to either supplied collection do not affect this calendar.</remarks>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int sharedCapacity, IReadOnlyList<DockBlackout> blackouts)
        : this(dockCapacities)
    {
        // Keep the legacy constructor's absent shared limit distinct from every finite capacity.
        if (sharedCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sharedCapacity), sharedCapacity, "Shared capacity must be positive.");
        }
        ArgumentNullException.ThrowIfNull(blackouts);

        // Copy before validating so all future searches use the same validated configuration.
        DockBlackout[] copiedBlackouts = blackouts.ToArray();
        foreach (DockBlackout? blackout in copiedBlackouts)
        {
            if (blackout is null ||
                (blackout.Dock is not null && !this.dockCapacities.ContainsKey(blackout.Dock)) ||
                blackout.StartMinute < 0 || blackout.EndMinute > 1440 || blackout.StartMinute >= blackout.EndMinute)
            {
                throw new ArgumentException("Each blackout needs a known dock or null and a valid interval.", nameof(blackouts));
            }
        }

        this.sharedCapacity = sharedCapacity;
        this.blackouts = copiedBlackouts;
    }

    /// <summary>Reserves a valid booking only if its ID is unused and every minute meets capacity and blackout restrictions.</summary>
    /// <param name="booking">A booking for a known dock, with nonblank ID, positive units, and 0 &lt;= start &lt; end &lt;= 1440.</param>
    /// <returns>True if added; false without changes for an existing ID, insufficient dock or shared capacity, or an overlapping blackout.</returns>
    /// <exception cref="ArgumentNullException">The booking is null.</exception>
    /// <exception cref="ArgumentException">The booking is malformed or names an unknown dock.</exception>
    public bool TryReserve(DockBooking booking)
    {
        // Validate before considering conflicts, so duplicate IDs do not conceal malformed bookings.
        ArgumentNullException.ThrowIfNull(booking);
        if (string.IsNullOrWhiteSpace(booking.Id) || string.IsNullOrWhiteSpace(booking.Dock) ||
            !dockCapacities.TryGetValue(booking.Dock, out int dockCapacity) ||
            booking.StartMinute < 0 || booking.EndMinute > 1440 || booking.EndMinute <= booking.StartMinute || booking.Units < 1)
        {
            throw new ArgumentException("Invalid booking.", nameof(booking));
        }

        // Search the exact interval, so accepting it requires every minute to satisfy the same rules as dispatch.
        if (bookings.ContainsKey(booking.Id) || booking.Units > dockCapacity ||
            FindEarliestStartMinute(booking.Dock, booking.StartMinute, booking.EndMinute,
                booking.EndMinute - booking.StartMinute, booking.Units) is null)
        {
            return false;
        }

        // Commit only after all minutes pass, leaving failed reservations without side effects.
        bookings.Add(booking.Id, booking);
        return true;
    }

    /// <summary>Removes the booking with the exact ordinal ID, freeing all its reserved capacity.</summary>
    /// <param name="id">The nonblank, case-sensitive booking identifier.</param>
    /// <returns>True if removed; false without changes if the ID is unknown.</returns>
    /// <exception cref="ArgumentNullException">The ID is null.</exception>
    /// <exception cref="ArgumentException">The ID is empty or whitespace.</exception>
    /// <remarks>Removing a booking frees both dock and shared capacity; configured blackouts remain in effect.</remarks>
    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return bookings.Remove(id);
    }

    /// <summary>Returns an independent list of immutable bookings sorted by ordinal ID.</summary>
    /// <returns>A list whose membership is unaffected by subsequent calendar changes.</returns>
    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(booking => booking.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Looks up a dock's capacity using the calendar's ordinal name comparison.</summary>
    /// <param name="dock">The dock name to look up.</param>
    /// <param name="capacityUnits">The configured capacity, or zero when the dock is unknown.</param>
    /// <returns>True if the dock exists.</returns>
    internal bool TryGetDockCapacity(string dock, out int capacityUnits) => dockCapacities.TryGetValue(dock, out capacityUnits);

    /// <summary>Checks whether an exact ordinal booking ID is already reserved without changing the calendar.</summary>
    /// <param name="id">The validated booking identifier.</param>
    /// <returns>True if the ID is in use on any dock.</returns>
    internal bool ContainsBookingId(string id) => bookings.ContainsKey(id);

    /// <summary>Finds the earliest interval satisfying dock capacity, shared capacity, and blackouts without reserving it.</summary>
    /// <param name="dock">A validated, known dock name.</param>
    /// <param name="earliestStartMinute">The inclusive start of a validated search window.</param>
    /// <param name="latestEndMinute">The latest permitted exclusive end minute, at most 1440.</param>
    /// <param name="durationMinutes">The validated positive number of consecutive minutes required.</param>
    /// <param name="requestedUnits">The validated positive units needed at each minute.</param>
    /// <returns>The earliest start minute, or null if no interval fits. Booking IDs are not considered.</returns>
    internal int? FindEarliestStartMinute(string dock, int earliestStartMinute, int latestEndMinute,
        int durationMinutes, int requestedUnits)
    {
        // Reject requests that cannot fit an empty window before allocating boundary arrays.
        int dockCapacityUnits = dockCapacities[dock];
        if (durationMinutes > latestEndMinute - earliestStartMinute || requestedUnits > dockCapacityUnits ||
            (sharedCapacity.HasValue && requestedUnits > sharedCapacity.Value))
        {
            return null;
        }

        // Track dock and shared usage separately; wide sums prevent overflow for large unit values.
        var dockUnitChanges = new long[latestEndMinute + 1];
        var sharedUnitChanges = sharedCapacity.HasValue ? new long[latestEndMinute + 1] : null;
        foreach (DockBooking booking in bookings.Values)
        {
            if (string.Equals(booking.Dock, dock, StringComparison.Ordinal))
            {
                AddIntervalChanges(dockUnitChanges, earliestStartMinute, latestEndMinute,
                    booking.StartMinute, booking.EndMinute, booking.Units);
            }
            if (sharedUnitChanges is not null)
            {
                AddIntervalChanges(sharedUnitChanges, earliestStartMinute, latestEndMinute,
                    booking.StartMinute, booking.EndMinute, booking.Units);
            }
        }

        // Count active blackouts so overlapping or unsorted intervals stay blocked until all have ended.
        var blackoutChanges = new long[latestEndMinute + 1];
        foreach (DockBlackout blackout in blackouts)
        {
            if (blackout.Dock is null || string.Equals(blackout.Dock, dock, StringComparison.Ordinal))
            {
                AddIntervalChanges(blackoutChanges, earliestStartMinute, latestEndMinute,
                    blackout.StartMinute, blackout.EndMinute, 1);
            }
        }

        // The first sufficiently long run is the earliest fit, including fits at exact end boundaries.
        long reservedDockUnits = 0;
        long reservedSharedUnits = 0;
        long activeBlackouts = 0;
        int availableRunMinutes = 0;
        for (int minute = earliestStartMinute; minute < latestEndMinute; minute++)
        {
            reservedDockUnits += dockUnitChanges[minute];
            reservedSharedUnits += sharedUnitChanges?[minute] ?? 0;
            activeBlackouts += blackoutChanges[minute];
            bool minuteAvailable = activeBlackouts == 0 && reservedDockUnits + requestedUnits <= dockCapacityUnits &&
                (!sharedCapacity.HasValue || reservedSharedUnits + requestedUnits <= sharedCapacity.Value);
            availableRunMinutes = minuteAvailable ? availableRunMinutes + 1 : 0;
            if (availableRunMinutes == durationMinutes)
            {
                return minute + 1 - durationMinutes;
            }
        }

        return null;
    }

    /// <summary>Adds an interval's value at its start and removes it at its end, clipped to a search window.</summary>
    /// <param name="minuteChanges">Boundary changes indexed by day minute, including the window's end.</param>
    /// <param name="windowStartMinute">The inclusive search boundary.</param>
    /// <param name="windowEndMinute">The exclusive search boundary.</param>
    /// <param name="intervalStartMinute">The inclusive interval boundary.</param>
    /// <param name="intervalEndMinute">The exclusive interval boundary.</param>
    /// <param name="intervalValue">The units or blackout count active throughout the interval.</param>
    private static void AddIntervalChanges(long[] minuteChanges, int windowStartMinute, int windowEndMinute,
        int intervalStartMinute, int intervalEndMinute, long intervalValue)
    {
        // Clipping carries intervals already in progress into the first searched minute.
        int overlapStartMinute = Math.Max(intervalStartMinute, windowStartMinute);
        int overlapEndMinute = Math.Min(intervalEndMinute, windowEndMinute);
        if (overlapStartMinute < overlapEndMinute)
        {
            minuteChanges[overlapStartMinute] += intervalValue;
            minuteChanges[overlapEndMinute] -= intervalValue;
        }
    }
}
