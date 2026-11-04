namespace DockScheduling;

/// <summary>Describes capacity requested on one dock during a half-open interval of day minutes.</summary>
/// <param name="Id">The nonblank, ordinal booking ID.</param>
/// <param name="Dock">The nonblank, ordinal name of an existing dock.</param>
/// <param name="StartMinute">The inclusive start minute, at or after zero.</param>
/// <param name="EndMinute">The exclusive end minute, after the start and at or before 1440.</param>
/// <param name="Units">The positive capacity units requested throughout the interval.</param>
/// <remarks>The calendar validates these values when a reservation is attempted.</remarks>
public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

/// <summary>Maintains one day's bookings with independent dock capacities and ordinal names and IDs.</summary>
/// <remarks>Use the calendar and any associated dispatchers sequentially.</remarks>
public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);

    /// <summary>Creates a calendar with a copy of the named dock capacities.</summary>
    /// <param name="dockCapacities">Nonblank dock names mapped to positive capacity units.</param>
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

    /// <summary>Reserves capacity at every minute in the booking's half-open interval.</summary>
    /// <returns>True if reserved; false without changes if the ID exists or capacity is insufficient.</returns>
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

        // Derive occupancy from current bookings so cancellation immediately frees capacity.
        // Widen sums before adding units to avoid overflow near the integer capacity limit.
        for (int minute = booking.StartMinute; minute < booking.EndMinute; minute++)
        {
            long reservedUnits = 0;
            foreach (var existingBooking in bookings.Values)
            {
                if (existingBooking.Dock == booking.Dock &&
                    existingBooking.StartMinute <= minute && minute < existingBooking.EndMinute)
                {
                    reservedUnits += existingBooking.Units;
                }
            }
            if (reservedUnits + booking.Units > dockCapacity)
            {
                return false;
            }
        }

        // Commit only after every minute fits, leaving rejected reservations without side effects.
        bookings.Add(booking.Id, booking);
        return true;
    }

    /// <summary>Removes the booking with the exact ordinal ID, freeing its reserved capacity.</summary>
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
}
