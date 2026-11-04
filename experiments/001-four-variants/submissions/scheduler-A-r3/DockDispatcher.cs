namespace DockScheduling;

public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

public enum DispatchPolicy
{
    BestEffort,
    AllOrNothing
}

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
        int dockCapacity = ValidateRequest(request, nameof(request));

        if (request.DurationMinutes > request.LatestEndMinute - request.EarliestStartMinute ||
            request.Units > dockCapacity || calendar.ContainsBooking(request.Id))
        {
            return null;
        }

        var availability = calendar.GetAvailability(request.Dock, request.Units, request.EarliestStartMinute, request.LatestEndMinute);
        int availableMinutes = 0;
        for (int minute = request.EarliestStartMinute; minute < request.LatestEndMinute; minute++)
        {
            availableMinutes = availability[minute] ? availableMinutes + 1 : 0;
            if (availableMinutes == request.DurationMinutes)
            {
                var booking = new DockBooking(request.Id, request.Dock, minute + 1 - request.DurationMinutes, minute + 1, request.Units);
                return calendar.TryReserve(booking) ? booking : null;
            }
        }

        return null;
    }

    public IReadOnlyList<DockBooking?> DispatchBatch(IReadOnlyList<DispatchRequest> requests, DispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (policy is not DispatchPolicy.BestEffort and not DispatchPolicy.AllOrNothing)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        var batch = requests.ToArray();
        foreach (var request in batch)
        {
            ValidateRequest(request, nameof(requests));
        }

        var results = new DockBooking?[batch.Length];
        for (int index = 0; index < batch.Length; index++)
        {
            results[index] = Dispatch(batch[index]);
            if (results[index] is null && policy == DispatchPolicy.AllOrNothing)
            {
                foreach (var booking in results)
                {
                    if (booking is not null)
                    {
                        calendar.Cancel(booking.Id);
                    }
                }
                Array.Clear(results);
                break;
            }
        }

        return results;
    }

    private int ValidateRequest(DispatchRequest? request, string parameterName)
    {
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
}
