namespace DockScheduling;

/// <summary>Describes a booking to place within a dock's permitted time window.</summary>
/// <param name="Id">The case-sensitive identifier for the resulting booking.</param>
/// <param name="Dock">The case-sensitive name of the requested dock.</param>
/// <param name="EarliestStartMinute">The inclusive earliest start minute, measured from midnight.</param>
/// <param name="LatestEndMinute">The inclusive deadline for the booking's exclusive end minute.</param>
/// <param name="DurationMinutes">The positive number of consecutive minutes to reserve.</param>
/// <param name="Units">The positive capacity units required at every reserved minute.</param>
public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

/// <summary>Finds and reserves the earliest available booking in a supplied calendar for sequential use.</summary>
public sealed class DockDispatcher
{
    private readonly DockCalendar calendar;

    /// <summary>Creates a dispatcher that reserves bookings in the supplied calendar.</summary>
    /// <param name="calendar">The calendar to search and update.</param>
    /// <exception cref="ArgumentNullException">The calendar is null.</exception>
    public DockDispatcher(DockCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        this.calendar = calendar;
    }

    /// <summary>Reserves the earliest start whose entire duration fits the requested window and capacity.</summary>
    /// <param name="request">A nonblank ID and known dock, a window within [0, 1440], and positive duration and units.</param>
    /// <returns>The reserved booking, or null without changes for an existing ID, an overlong duration, or no capacity fit.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The ID, dock, window, duration, or units are invalid.</exception>
    public DockBooking? Dispatch(DispatchRequest request)
    {
        // Validate the whole request before returning any ordinary scheduling failure.
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.TryGetDockCapacity(request.Dock, out int dockCapacityUnits) ||
            request.EarliestStartMinute < 0 || request.LatestEndMinute > 1440 ||
            request.EarliestStartMinute >= request.LatestEndMinute ||
            request.DurationMinutes < 1 || request.Units < 1)
        {
            throw new ArgumentException("Invalid dispatch request.", nameof(request));
        }

        // These valid requests cannot fit even an empty dock; subtraction is safe after window validation.
        if (request.DurationMinutes > request.LatestEndMinute - request.EarliestStartMinute ||
            request.Units > dockCapacityUnits)
        {
            return null;
        }

        // Record usage changes at interval boundaries, clipped to the search window.
        // A wider accumulator keeps capacity comparisons safe even for int.MaxValue units.
        var reservedUnitChanges = new long[request.LatestEndMinute + 1];
        foreach (var existing in calendar.Snapshot())
        {
            // IDs are global across docks, so check them before filtering for the requested dock.
            if (string.Equals(existing.Id, request.Id, StringComparison.Ordinal))
            {
                return null;
            }
            if (!string.Equals(existing.Dock, request.Dock, StringComparison.Ordinal))
            {
                continue;
            }

            int overlapStartMinute = Math.Max(existing.StartMinute, request.EarliestStartMinute);
            int overlapEndMinute = Math.Min(existing.EndMinute, request.LatestEndMinute);
            if (overlapStartMinute < overlapEndMinute)
            {
                reservedUnitChanges[overlapStartMinute] += existing.Units;
                reservedUnitChanges[overlapEndMinute] -= existing.Units;
            }
        }

        // Accumulate usage while tracking a consecutive run of minutes with enough free units.
        // The first run reaching the duration gives the earliest start, including exact end-boundary fits.
        long reservedUnits = 0;
        int availableRunMinutes = 0;
        for (int minute = request.EarliestStartMinute; minute < request.LatestEndMinute; minute++)
        {
            reservedUnits += reservedUnitChanges[minute];
            availableRunMinutes = reservedUnits + request.Units <= dockCapacityUnits ? availableRunMinutes + 1 : 0;
            if (availableRunMinutes == request.DurationMinutes)
            {
                // Only the selected interval is submitted for reservation; searching never mutates the calendar.
                int endMinute = minute + 1;
                var booking = new DockBooking(request.Id, request.Dock, endMinute - request.DurationMinutes, endMinute, request.Units);
                return calendar.TryReserve(booking) ? booking : null;
            }
        }

        return null;
    }
}
