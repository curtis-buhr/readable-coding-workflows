namespace DockScheduling;

/// <summary>Describes capacity units to reserve on a named dock over a half-open interval.</summary>
/// <param name="Id">The case-sensitive booking identifier.</param>
/// <param name="Dock">The case-sensitive dock name.</param>
/// <param name="StartMinute">The inclusive start minute, measured from midnight.</param>
/// <param name="EndMinute">The exclusive end minute, measured from midnight.</param>
/// <param name="Units">The capacity units reserved at every minute of the interval.</param>
public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

/// <summary>Maintains one day's bookings and independent dock capacities for sequential use.</summary>
public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);

    /// <summary>Copies the supplied capacities, comparing dock names using ordinal case-sensitive equality.</summary>
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

    /// <summary>Reserves a valid booking only if its ID is unused and every covered minute has capacity.</summary>
    /// <param name="booking">A booking for a known dock, with nonblank ID, positive units, and 0 &lt;= start &lt; end &lt;= 1440.</param>
    /// <returns>True if added; false without changes if the ID exists or capacity is insufficient.</returns>
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

        if (bookings.ContainsKey(booking.Id) || booking.Units > dockCapacity)
        {
            return false;
        }

        // Sum simultaneous usage per minute; pairwise overlap checks would miss combined overloads.
        // Use a wider sum so large capacity values cannot overflow during the comparison.
        for (int minute = booking.StartMinute; minute < booking.EndMinute; minute++)
        {
            long reservedUnits = 0;
            foreach (var existing in bookings.Values)
            {
                if (string.Equals(existing.Dock, booking.Dock, StringComparison.Ordinal) &&
                    existing.StartMinute <= minute && minute < existing.EndMinute)
                {
                    reservedUnits += existing.Units;
                }
            }
            if (reservedUnits + booking.Units > dockCapacity)
            {
                return false;
            }
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
}
