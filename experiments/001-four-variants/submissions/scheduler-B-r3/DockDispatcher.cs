namespace DockScheduling;

/// <summary>Describes a booking to place within a dock's permitted time window.</summary>
/// <param name="Id">The case-sensitive identifier for the resulting booking.</param>
/// <param name="Dock">The case-sensitive name of the requested dock.</param>
/// <param name="EarliestStartMinute">The inclusive earliest start minute, measured from midnight.</param>
/// <param name="LatestEndMinute">The inclusive deadline for the booking's exclusive end minute.</param>
/// <param name="DurationMinutes">The positive number of consecutive minutes to reserve.</param>
/// <param name="Units">The positive capacity units required at every reserved minute.</param>
public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

/// <summary>Controls whether a batch retains successful bookings when a request cannot be scheduled.</summary>
public enum DispatchPolicy
{
    /// <summary>Retains successful bookings and continues after each scheduling failure.</summary>
    BestEffort,

    /// <summary>Removes all bookings made by the batch if any request cannot be scheduled.</summary>
    AllOrNothing
}

/// <summary>Reserves earliest fits respecting a calendar's dock capacity, shared capacity, and blackouts for sequential use.</summary>
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

    /// <summary>Reserves the earliest start whose entire duration fits the window, all capacity limits, and blackouts.</summary>
    /// <param name="request">A nonblank ID and known dock, a window within [0, 1440], and positive duration and units.</param>
    /// <returns>The reserved booking, or null without changes for an existing ID, an overlong duration, or no available interval.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The ID, dock, window, duration, or units are invalid.</exception>
    public DockBooking? Dispatch(DispatchRequest request)
    {
        // Validate the whole request before returning any ordinary scheduling failure.
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, nameof(request));

        // IDs are global across docks; availability searching can ignore them after this check.
        if (calendar.ContainsBookingId(request.Id))
        {
            return null;
        }

        // Share the calendar's search so every reservation path observes all capacity and blackout restrictions.
        int? startMinute = calendar.FindEarliestStartMinute(request.Dock, request.EarliestStartMinute,
            request.LatestEndMinute, request.DurationMinutes, request.Units);
        if (startMinute is null)
        {
            return null;
        }

        // Searching is read-only; submit only the chosen interval to commit the reservation.
        var booking = new DockBooking(request.Id, request.Dock, startMinute.Value,
            startMinute.Value + request.DurationMinutes, request.Units);
        return calendar.TryReserve(booking) ? booking : null;
    }

    /// <summary>Validates the entire batch, then schedules requests in input order using earlier successes.</summary>
    /// <param name="requests">Requests with the same validity rules as Dispatch. Empty batches are accepted.</param>
    /// <param name="policy">Whether to retain individual successes or undo the batch on a scheduling failure.</param>
    /// <returns>One booking or null per input, with all entries null if an AllOrNothing batch fails.</returns>
    /// <exception cref="ArgumentNullException">The requests collection is null.</exception>
    /// <exception cref="ArgumentException">An entry is null or malformed. The calendar remains unchanged.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is unknown. The calendar remains unchanged.</exception>
    /// <remarks>Each request observes dock and shared capacity and blackouts. Existing bookings are preserved.
    /// A successful request claims its ID for later requests in the batch.</remarks>
    public IReadOnlyList<DockBooking?> DispatchBatch(IReadOnlyList<DispatchRequest> requests, DispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (policy != DispatchPolicy.BestEffort && policy != DispatchPolicy.AllOrNothing)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown dispatch policy.");
        }

        // Copy the inputs so scheduling uses exactly the requests validated before any reservation.
        DispatchRequest[] validatedRequests = requests.ToArray();
        foreach (DispatchRequest request in validatedRequests)
        {
            ValidateRequest(request, nameof(requests));
        }

        // Reuse Dispatch so capacity, earliest starts, and duplicate IDs follow the single-request rules.
        var batchBookings = new DockBooking?[validatedRequests.Length];
        for (int requestIndex = 0; requestIndex < validatedRequests.Length; requestIndex++)
        {
            batchBookings[requestIndex] = Dispatch(validatedRequests[requestIndex]);
            if (batchBookings[requestIndex] is not null || policy == DispatchPolicy.BestEffort)
            {
                continue;
            }

            // Only successful additions from this batch are cancelled, preserving every existing booking.
            foreach (DockBooking? batchBooking in batchBookings)
            {
                if (batchBooking is not null)
                {
                    calendar.Cancel(batchBooking.Id);
                }
            }
            return new DockBooking?[validatedRequests.Length];
        }

        return batchBookings;
    }

    /// <summary>Validates a request, including its dock's existence, without changing the calendar.</summary>
    /// <param name="request">The request to validate, including possibly null batch entries.</param>
    /// <param name="parameterName">The caller's parameter name to report for invalid input.</param>
    /// <exception cref="ArgumentException">The request is null or has an invalid ID, dock, window, duration, or units.</exception>
    private void ValidateRequest(DispatchRequest? request, string parameterName)
    {
        // Keep malformed input separate from ordinary no-fit outcomes, including oversized positive requests.
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.TryGetDockCapacity(request.Dock, out _) ||
            request.EarliestStartMinute < 0 || request.LatestEndMinute > 1440 ||
            request.EarliestStartMinute >= request.LatestEndMinute ||
            request.DurationMinutes < 1 || request.Units < 1)
        {
            throw new ArgumentException("Invalid dispatch request.", parameterName);
        }
    }
}
