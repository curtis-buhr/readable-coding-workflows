namespace DockScheduling;

/// <summary>Controls whether a batch retains successful bookings when a request cannot be scheduled.</summary>
public enum DispatchPolicy
{
    /// <summary>Keeps successful bookings and continues scheduling after a request fails.</summary>
    BestEffort,

    /// <summary>Removes the batch's successful bookings if any request fails.</summary>
    AllOrNothing
}

/// <summary>Describes a booking to place at the earliest available start within a day-minute window.</summary>
/// <param name="Id">The nonblank, ordinal booking ID to reserve.</param>
/// <param name="Dock">The nonblank, ordinal name of an existing dock.</param>
/// <param name="EarliestStartMinute">The inclusive earliest start, at or after minute zero.</param>
/// <param name="LatestEndMinute">The latest allowed end, after the earliest start and at or before 1440.</param>
/// <param name="DurationMinutes">The positive number of consecutive minutes to reserve.</param>
/// <param name="Units">The positive capacity units needed throughout the booking.</param>
/// <remarks>The dispatcher validates these values when dispatch is attempted.</remarks>
public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

/// <summary>Finds and reserves the earliest available interval in a supplied dock calendar.</summary>
/// <remarks>Use the dispatcher and its calendar sequentially.</remarks>
public sealed class DockDispatcher
{
    private readonly DockCalendar calendar;

    /// <summary>Creates a dispatcher that reserves bookings directly in the supplied calendar.</summary>
    /// <param name="calendar">The calendar to search and update on each successful dispatch.</param>
    /// <exception cref="ArgumentNullException">The calendar is null.</exception>
    public DockDispatcher(DockCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        this.calendar = calendar;
    }

    /// <summary>Reserves the earliest interval satisfying the request's window, duration, and units.</summary>
    /// <param name="request">The booking details and nonempty search window within minutes 0 through 1440.</param>
    /// <returns>The reserved booking, or null without changes if its ID exists or no interval fits.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">
    /// The request has a blank ID or dock, an unknown dock, an invalid day-minute window,
    /// or nonpositive duration or units. Validation precedes any reservation or no-fit result.
    /// </exception>
    public DockBooking? Dispatch(DispatchRequest request)
    {
        // Validate the complete request before considering duplicate IDs or impossible fits.
        ArgumentNullException.ThrowIfNull(request);
        int dockCapacity = ValidateRequest(request, nameof(request));

        // Subtract only validated bounds; a positive duration may exceed this window without being malformed.
        int windowDurationMinutes = request.LatestEndMinute - request.EarliestStartMinute;
        if (request.DurationMinutes > windowDurationMinutes || request.Units > dockCapacity)
        {
            return null;
        }

        // A snapshot covers both global ID uniqueness and current occupancy on the requested dock.
        IReadOnlyList<DockBooking> existingBookings = calendar.Snapshot();
        if (existingBookings.Any(booking => StringComparer.Ordinal.Equals(booking.Id, request.Id)))
        {
            return null;
        }

        int? startMinute = FindEarliestStart(request, dockCapacity, existingBookings);
        if (startMinute is not int acceptedStartMinute)
        {
            return null;
        }

        // Search is read-only; let the calendar enforce its reservation contract for the final commit.
        var acceptedBooking = new DockBooking(request.Id, request.Dock, acceptedStartMinute,
            acceptedStartMinute + request.DurationMinutes, request.Units);
        return calendar.TryReserve(acceptedBooking) ? acceptedBooking : null;
    }

    /// <summary>Schedules requests in input order using the supplied policy for scheduling failures.</summary>
    /// <param name="requests">Requests to validate in full before making any reservations.</param>
    /// <param name="policy">Whether to retain successful reservations when a request cannot be scheduled.</param>
    /// <returns>
    /// One booking or null per input. BestEffort retains successes and returns null for failures.
    /// AllOrNothing returns all null entries after any failure and removes this batch's bookings.
    /// Empty batches return empty lists.
    /// </returns>
    /// <remarks>Malformed batches leave the calendar unchanged. Existing bookings survive either policy.</remarks>
    /// <exception cref="ArgumentNullException">The requests collection is null.</exception>
    /// <exception cref="ArgumentException">An entry is null or has malformed request values.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is unknown.</exception>
    public IReadOnlyList<DockBooking?> DispatchBatch(IReadOnlyList<DispatchRequest> requests, DispatchPolicy policy)
    {
        // Reject invalid batch arguments before reading entries or changing the calendar.
        ArgumentNullException.ThrowIfNull(requests);
        if (policy != DispatchPolicy.BestEffort && policy != DispatchPolicy.AllOrNothing)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown dispatch policy.");
        }

        // Copy the entries so validation and scheduling operate on the same requests.
        DispatchRequest[] batchRequests = requests.ToArray();
        foreach (DispatchRequest request in batchRequests)
        {
            ValidateRequest(request, nameof(requests));
        }

        // Reuse single dispatch so each request sees all earlier successful reservations.
        var acceptedBookings = new DockBooking?[batchRequests.Length];
        for (int requestIndex = 0; requestIndex < batchRequests.Length; requestIndex++)
        {
            acceptedBookings[requestIndex] = Dispatch(batchRequests[requestIndex]);
            if (acceptedBookings[requestIndex] is null && policy == DispatchPolicy.AllOrNothing)
            {
                // Only this batch's successful reservations own these IDs, preserving existing bookings.
                foreach (DockBooking? acceptedBooking in acceptedBookings)
                {
                    if (acceptedBooking is not null)
                    {
                        calendar.Cancel(acceptedBooking.Id);
                    }
                }

                // Clearing successes makes the result match the rolled-back calendar.
                Array.Clear(acceptedBookings);
                break;
            }
        }

        return acceptedBookings;
    }

    /// <summary>Checks request structure without changing the calendar and returns the dock capacity.</summary>
    /// <param name="request">The request to validate, including a possible null batch entry.</param>
    /// <param name="parameterName">The public argument name to report for malformed values.</param>
    /// <returns>The existing dock's capacity in units. Excess duration or units remain valid no-fit requests.</returns>
    /// <exception cref="ArgumentException">The request is null or contains malformed values.</exception>
    private int ValidateRequest(DispatchRequest? request, string parameterName)
    {
        // Share structural validation across both APIs before duplicate IDs or no-fit checks can return.
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.TryGetDockCapacity(request.Dock, out int dockCapacity) ||
            request.EarliestStartMinute < 0 || request.LatestEndMinute > 1440 ||
            request.EarliestStartMinute >= request.LatestEndMinute ||
            request.DurationMinutes < 1 || request.Units < 1)
        {
            throw new ArgumentException("Invalid dispatch request.", parameterName);
        }

        return dockCapacity;
    }

    /// <summary>Finds the first fitting start for a validated request without changing the calendar.</summary>
    /// <param name="request">A valid request whose duration fits its window and whose units fit the dock capacity.</param>
    /// <param name="dockCapacity">The requested dock's capacity in units.</param>
    /// <param name="existingBookings">Current bookings, including those on other docks.</param>
    /// <returns>The earliest fitting start minute, or null if occupancy prevents a fit.</returns>
    private static int? FindEarliestStart(DispatchRequest request, int dockCapacity, IReadOnlyList<DockBooking> existingBookings)
    {
        // Record occupancy changes at clipped endpoints. Long arithmetic keeps sums safe for large units.
        var reservedUnitChanges = new long[request.LatestEndMinute + 1];
        foreach (DockBooking booking in existingBookings)
        {
            if (!StringComparer.Ordinal.Equals(booking.Dock, request.Dock))
            {
                continue;
            }

            int overlapStartMinute = Math.Max(booking.StartMinute, request.EarliestStartMinute);
            int overlapEndMinute = Math.Min(booking.EndMinute, request.LatestEndMinute);
            if (overlapStartMinute < overlapEndMinute)
            {
                reservedUnitChanges[overlapStartMinute] += booking.Units;
                reservedUnitChanges[overlapEndMinute] -= booking.Units;
            }
        }

        // Applying endpoint changes before each minute preserves half-open intervals and adjacent bookings.
        long reservedUnits = 0;
        int consecutiveAvailableMinutes = 0;
        for (int minute = request.EarliestStartMinute; minute < request.LatestEndMinute; minute++)
        {
            reservedUnits += reservedUnitChanges[minute];
            consecutiveAvailableMinutes = reservedUnits + request.Units <= dockCapacity
                ? consecutiveAvailableMinutes + 1
                : 0;

            // The first sufficiently long run ends at the earliest possible fitting interval.
            if (consecutiveAvailableMinutes == request.DurationMinutes)
            {
                return minute - request.DurationMinutes + 1;
            }
        }

        return null;
    }
}
