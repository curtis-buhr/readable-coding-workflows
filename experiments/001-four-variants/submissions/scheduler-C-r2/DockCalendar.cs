namespace DockScheduling;

public sealed record DockBooking(string Id, string Dock, int StartMinute, int EndMinute, int Units);

public sealed class DockCalendar
{
    private readonly Dictionary<string, int> dockCapacities;
    private readonly Dictionary<string, DockBooking> bookings = new(StringComparer.Ordinal);

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

        for (int minute = booking.StartMinute; minute < booking.EndMinute; minute++)
        {
            long reserved = 0;
            foreach (var existing in bookings.Values)
            {
                if (existing.Dock == booking.Dock && existing.StartMinute <= minute && minute < existing.EndMinute)
                {
                    reserved += existing.Units;
                }
            }
            if (reserved + booking.Units > dockCapacity)
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

    public IReadOnlyList<DockBooking> Snapshot() => bookings.Values.OrderBy(booking => booking.Id, StringComparer.Ordinal).ToArray();

    internal bool TryGetDockCapacity(string dock, out int capacity) => dockCapacities.TryGetValue(dock, out capacity);
}
