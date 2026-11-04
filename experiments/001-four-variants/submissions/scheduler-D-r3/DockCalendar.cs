namespace DockScheduling;

/// <summary>Describes capacity requested on one dock during a half-open interval of day minutes.</summary>
/// <param name="Id">The nonblank, ordinal booking ID.</param>
/// <param name="Dock">The nonblank, ordinal name of an existing dock.</param>
/// <param name="StartMinute">The inclusive start minute, at or after zero.</param>
/// <param name="EndMinute">The exclusive end minute, after the start and at or before 1440.</param>
/// <param name="Units">The positive capacity units requested throughout the interval.</param>
/// <remarks>The calendar validates these values when a reservation is attempted.</remarks>
public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

/// <summary>Blocks reservations during a half-open interval of day minutes.</summary>
/// <param name="Dock">The ordinal name of an existing dock, or null to block all docks.</param>
/// <param name="StartMinute">The inclusive start minute, at or after zero.</param>
/// <param name="EndMinute">The exclusive end minute, after the start and at or before 1440.</param>
/// <remarks>The calendar validates these values when its configuration is constructed.</remarks>
public sealed record DockBlackout(string? Dock, int StartMinute, int EndMinute);

/// <summary>Maintains one day's bookings with dock capacities, optional shared limits, and blackouts.</summary>
/// <remarks>Dock names and booking IDs are ordinal and case-sensitive. Use the calendar and dispatchers sequentially.</remarks>
public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);
    private readonly int? sharedCapacity;
    private readonly DockBlackout[] blackouts = [];

    /// <summary>Copies the named dock capacities, with no shared limit or blackouts.</summary>
    /// <param name="dockCapacities">Nonblank dock names mapped to positive capacity units.</param>
    /// <remarks>Combined usage across docks may exceed Int32.MaxValue.</remarks>
    /// <exception cref="ArgumentNullException">The capacity dictionary is null.</exception>
    /// <exception cref="ArgumentException">A dock name is blank or its capacity is not positive.</exception>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities)
    {
        // Copy configuration so later caller edits cannot change capacity or name comparison.
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

    /// <summary>Copies dock capacities and blackouts and applies a shared limit across all docks.</summary>
    /// <param name="dockCapacities">Nonblank, ordinal dock names mapped to positive capacity units.</param>
    /// <param name="sharedCapacity">Positive total capacity units available across all docks at each minute.</param>
    /// <param name="blackouts">Non-null entries blocking known docks, or all docks, during valid day-minute intervals.</param>
    /// <exception cref="ArgumentNullException">The capacity dictionary or blackout collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The shared capacity is not positive.</exception>
    /// <exception cref="ArgumentException">A dock capacity is invalid, or a blackout is null, names an unknown dock, or has an invalid interval.</exception>
    public DockCalendar(IReadOnlyDictionary<string, int> dockCapacities, int sharedCapacity,
        IReadOnlyList<DockBlackout> blackouts) : this(dockCapacities)
    {
        // An absent shared limit belongs only to the original constructor; supplied limits must be positive.
        if (sharedCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sharedCapacity), sharedCapacity, "Shared capacity must be positive.");
        }
        ArgumentNullException.ThrowIfNull(blackouts);

        // Validate the copied entries against copied dock names so later caller edits cannot change restrictions.
        this.sharedCapacity = sharedCapacity;
        this.blackouts = blackouts.ToArray();
        foreach (DockBlackout? blackout in this.blackouts)
        {
            if (blackout is null ||
                (blackout.Dock is not null && !this.dockCapacities.ContainsKey(blackout.Dock)) ||
                blackout.StartMinute < 0 || blackout.EndMinute > 1440 ||
                blackout.StartMinute >= blackout.EndMinute)
            {
                throw new ArgumentException("Invalid blackout.", nameof(blackouts));
            }
        }
    }

    /// <summary>Reserves capacity at every minute in the booking's half-open interval.</summary>
    /// <param name="booking">The booking to validate and reserve against dock, shared, and blackout restrictions.</param>
    /// <returns>True if reserved; false without changes if the ID exists, capacity is insufficient, or a blackout overlaps.</returns>
    /// <exception cref="ArgumentNullException">The booking is null.</exception>
    /// <exception cref="ArgumentException">
    /// The booking has a blank ID or dock, an unknown dock, nonpositive units,
    /// or an interval that is empty, reversed, or outside 0 to 1440.
    /// </exception>
    public bool TryReserve(DockBooking booking)
    {
        // Validate first so a duplicate ID cannot hide a malformed booking.
        ArgumentNullException.ThrowIfNull(booking);
        if (string.IsNullOrWhiteSpace(booking.Id) || string.IsNullOrWhiteSpace(booking.Dock) ||
            !dockCapacities.TryGetValue(booking.Dock, out int dockCapacity) ||
            booking.StartMinute < 0 || booking.EndMinute > 1440 ||
            booking.EndMinute <= booking.StartMinute || booking.Units < 1)
        {
            throw new ArgumentException("Invalid booking.", nameof(booking));
        }

        if (bookings.ContainsKey(booking.Id) || booking.Units > dockCapacity)
        {
            return false;
        }

        // The exact booking window uses the same restriction checks as dispatch searches.
        int durationMinutes = booking.EndMinute - booking.StartMinute;
        if (FindEarliestStart(booking.Dock, booking.StartMinute, booking.EndMinute,
            durationMinutes, booking.Units) is null)
        {
            return false;
        }

        // Commit only after every minute fits, leaving rejected reservations without side effects.
        bookings.Add(booking.Id, booking);
        return true;
    }

    /// <summary>Removes the booking with the exact ordinal ID, freeing dock and shared capacity while retaining blackouts.</summary>
    /// <returns>True if a booking was removed; false if the ID was unknown.</returns>
    /// <exception cref="ArgumentNullException">The ID is null.</exception>
    /// <exception cref="ArgumentException">The ID is blank.</exception>
    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return bookings.Remove(id);
    }

    /// <summary>Returns an independent list of immutable bookings sorted by ordinal ID.</summary>
    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(booking => booking.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Looks up a dock's capacity using the calendar's ordinal name comparison.</summary>
    /// <returns>True with the configured capacity, or false with zero for an unknown dock.</returns>
    internal bool TryGetDockCapacity(string dock, out int dockCapacity) => dockCapacities.TryGetValue(dock, out dockCapacity);

    /// <summary>Finds the earliest interval satisfying all calendar restrictions without changing bookings.</summary>
    /// <param name="dock">A known ordinal dock name.</param>
    /// <param name="earliestStartMinute">The inclusive start of a validated, nonempty day-minute window.</param>
    /// <param name="latestEndMinute">The exclusive end of the window, at or before minute 1440.</param>
    /// <param name="durationMinutes">The positive consecutive duration required within the window.</param>
    /// <param name="requestedUnits">The positive capacity units required at each minute.</param>
    /// <returns>The earliest fitting start minute, or null if no interval fits.</returns>
    internal int? FindEarliestStart(string dock, int earliestStartMinute, int latestEndMinute,
        int durationMinutes, int requestedUnits)
    {
        // Separate dock and shared endpoint totals retain independent dock limits when no shared limit exists.
        var dockUnitChanges = new long[latestEndMinute + 1];
        var sharedUnitChanges = new long[latestEndMinute + 1];
        var blackoutCountChanges = new long[latestEndMinute + 1];
        foreach (DockBooking booking in bookings.Values)
        {
            if (StringComparer.Ordinal.Equals(booking.Dock, dock))
            {
                AddIntervalChanges(dockUnitChanges, booking.StartMinute, booking.EndMinute,
                    earliestStartMinute, latestEndMinute, booking.Units);
            }
            if (sharedCapacity.HasValue)
            {
                AddIntervalChanges(sharedUnitChanges, booking.StartMinute, booking.EndMinute,
                    earliestStartMinute, latestEndMinute, booking.Units);
            }
        }

        // Count overlapping blackouts so an earlier ending interval cannot clear a still-active block.
        foreach (DockBlackout blackout in blackouts)
        {
            if (blackout.Dock is null || StringComparer.Ordinal.Equals(blackout.Dock, dock))
            {
                AddIntervalChanges(blackoutCountChanges, blackout.StartMinute, blackout.EndMinute,
                    earliestStartMinute, latestEndMinute, 1);
            }
        }

        // Applying clipped endpoints before each minute preserves half-open boundaries; long sums avoid overflow.
        long dockReservedUnits = 0;
        long sharedReservedUnits = 0;
        long activeBlackoutCount = 0;
        int consecutiveAvailableMinutes = 0;
        for (int minute = earliestStartMinute; minute < latestEndMinute; minute++)
        {
            dockReservedUnits += dockUnitChanges[minute];
            sharedReservedUnits += sharedUnitChanges[minute];
            activeBlackoutCount += blackoutCountChanges[minute];
            bool minuteAvailable = activeBlackoutCount == 0 &&
                dockReservedUnits + requestedUnits <= dockCapacities[dock] &&
                (!sharedCapacity.HasValue || sharedReservedUnits + requestedUnits <= sharedCapacity.Value);
            consecutiveAvailableMinutes = minuteAvailable ? consecutiveAvailableMinutes + 1 : 0;

            // The first sufficiently long run yields the earliest start, including after any kind of restriction.
            if (consecutiveAvailableMinutes == durationMinutes)
            {
                return minute - durationMinutes + 1;
            }
        }

        return null;
    }

    /// <summary>Adds a quantity's half-open interval to endpoint changes, clipped to the search window.</summary>
    /// <param name="minuteChanges">Changes indexed by absolute day minute, including the window's end endpoint.</param>
    /// <param name="startMinute">The validated inclusive interval start.</param>
    /// <param name="endMinute">The validated exclusive interval end.</param>
    /// <param name="windowStartMinute">The inclusive search-window start.</param>
    /// <param name="windowEndMinute">The exclusive search-window end.</param>
    /// <param name="quantity">The positive units or blackout count active throughout the interval.</param>
    private static void AddIntervalChanges(long[] minuteChanges, int startMinute, int endMinute,
        int windowStartMinute, int windowEndMinute, int quantity)
    {
        // Clipping carries intervals already active at the search start into the first minute's totals.
        int overlapStartMinute = Math.Max(startMinute, windowStartMinute);
        int overlapEndMinute = Math.Min(endMinute, windowEndMinute);
        if (overlapStartMinute < overlapEndMinute)
        {
            minuteChanges[overlapStartMinute] += quantity;
            minuteChanges[overlapEndMinute] -= quantity;
        }
    }
}
