namespace DockScheduling;

public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

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
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.TryGetDockCapacity(request.Dock, out int dockCapacity) ||
            request.EarliestStartMinute < 0 || request.LatestEndMinute > 1440 ||
            request.EarliestStartMinute >= request.LatestEndMinute ||
            request.DurationMinutes < 1 || request.Units < 1)
        {
            throw new ArgumentException("Invalid dispatch request.", nameof(request));
        }

        if (request.DurationMinutes > request.LatestEndMinute - request.EarliestStartMinute || request.Units > dockCapacity)
        {
            return null;
        }

        // Capacity changes at booking endpoints preserve the half-open intervals.
        var capacityChanges = new long[1441];
        foreach (var booking in calendar.Snapshot())
        {
            if (booking.Id == request.Id)
            {
                return null;
            }
            if (booking.Dock == request.Dock)
            {
                int start = Math.Max(booking.StartMinute, request.EarliestStartMinute);
                int end = Math.Min(booking.EndMinute, request.LatestEndMinute);
                if (start < end)
                {
                    capacityChanges[start] += booking.Units;
                    capacityChanges[end] -= booking.Units;
                }
            }
        }

        long reserved = 0;
        int availableMinutes = 0;
        for (int minute = request.EarliestStartMinute; minute < request.LatestEndMinute; minute++)
        {
            reserved += capacityChanges[minute];
            availableMinutes = reserved + request.Units <= dockCapacity ? availableMinutes + 1 : 0;
            if (availableMinutes == request.DurationMinutes)
            {
                var booking = new DockBooking(request.Id, request.Dock, minute + 1 - request.DurationMinutes, minute + 1, request.Units);
                return calendar.TryReserve(booking) ? booking : null;
            }
        }

        return null;
    }
}
