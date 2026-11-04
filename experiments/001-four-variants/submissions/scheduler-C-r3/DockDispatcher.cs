namespace DockScheduling;

public sealed record DispatchRequest(string Id, string Dock, int EarliestStartMinute, int LatestEndMinute, int DurationMinutes, int Units);

public enum DispatchPolicy { BestEffort, AllOrNothing }

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
        ValidateRequest(request, nameof(request));

        if (calendar.Snapshot().Any(booking => string.Equals(booking.Id, request.Id, StringComparison.Ordinal)))
        {
            return null;
        }

        int? start = calendar.FindEarliestStart(request.Dock, request.EarliestStartMinute,
            request.LatestEndMinute, request.DurationMinutes, request.Units);
        if (start is null)
        {
            return null;
        }

        var booking = new DockBooking(request.Id, request.Dock, start.Value, start.Value + request.DurationMinutes, request.Units);
        return calendar.TryReserve(booking) ? booking : null;
    }

    public IReadOnlyList<DockBooking?> DispatchBatch(IReadOnlyList<DispatchRequest> requests, DispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (policy is not DispatchPolicy.BestEffort and not DispatchPolicy.AllOrNothing)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown dispatch policy.");
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
                return results;
            }
        }

        return results;
    }

    private void ValidateRequest(DispatchRequest request, string parameterName)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Dock) ||
            !calendar.TryGetDockCapacity(request.Dock, out _) ||
            request.EarliestStartMinute < 0 || request.LatestEndMinute > 1440 ||
            request.EarliestStartMinute >= request.LatestEndMinute || request.DurationMinutes < 1 || request.Units < 1)
        {
            throw new ArgumentException("Invalid dispatch request.", parameterName);
        }
    }
}
