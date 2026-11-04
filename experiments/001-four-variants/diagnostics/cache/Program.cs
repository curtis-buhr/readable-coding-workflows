using System.Text.Json;
using ProductCatalog;

var results = Enum.GetValues<RefreshAction>().Select(RunCase);
Console.WriteLine(JsonSerializer.Serialize(results));

static CaseResult RunCase(RefreshAction action)
{
    const string key = "item-42";
    var clock = new ScriptedClock();
    var loadFailure = new CatalogUnavailableException("refresh loader threw");
    var clockFailure = new CatalogUnavailableException("post-load timestamp read threw");
    var loadCount = 0;
    bool? invalidationRemovedEntry = null;
    var clearCalled = false;
    var loaderReturnedRefresh = false;
    CatalogCache? cache = null;
    cache = new CatalogCache(_ =>
    {
        if (++loadCount == 1)
            return "seed";

        switch (action)
        {
            case RefreshAction.InvalidateThenThrow:
                invalidationRemovedEntry = cache!.Invalidate(key);
                throw loadFailure;
            case RefreshAction.ClearThenThrow:
                cache!.Clear();
                clearCalled = true;
                throw loadFailure;
            case RefreshAction.ReturnThenClockThrows:
                clock.NextFailure = clockFailure;
                loaderReturnedRefresh = true;
                return "refresh";
            case RefreshAction.ThrowWithEntryRetained:
                throw loadFailure;
            default:
                throw new InvalidOperationException($"Unexpected refresh action {action}.");
        }
    }, clock);

    var seed = cache.Get(key);
    if (seed != "seed" || loadCount != 1)
        throw new InvalidOperationException("The seed read did not load the expected value.");

    clock.Now += TimeSpan.FromSeconds(45);
    var expected = action switch
    {
        RefreshAction.ThrowWithEntryRetained => "returned:seed:stale",
        RefreshAction.ReturnThenClockThrows => "threw:timestamp",
        _ => "threw:loader",
    };

    string observed;
    try
    {
        var result = cache.Read(key, FreshnessPolicy.AllowStaleOnTransientFailure);
        observed = $"returned:{result.Value}:{(result.IsStale ? "stale" : "fresh")}";
    }
    catch (CatalogUnavailableException failure) when (ReferenceEquals(failure, loadFailure))
    {
        observed = "threw:loader";
    }
    catch (CatalogUnavailableException failure) when (ReferenceEquals(failure, clockFailure))
    {
        observed = "threw:timestamp";
    }
    catch (Exception failure)
    {
        observed = $"threw:{failure.GetType().FullName}:{failure.Message}";
    }

    var setupMatched = loadCount == 2 && action switch
    {
        RefreshAction.InvalidateThenThrow => invalidationRemovedEntry == true,
        RefreshAction.ClearThenThrow => clearCalled,
        RefreshAction.ReturnThenClockThrows => loaderReturnedRefresh && clock.FailureCount == 1,
        _ => true,
    };
    return new CaseResult(action.ToString(), expected, observed, setupMatched,
        setupMatched && observed == expected, loadCount, invalidationRemovedEntry, clearCalled,
        loaderReturnedRefresh, clock.FailureCount);
}

enum RefreshAction
{
    ThrowWithEntryRetained,
    InvalidateThenThrow,
    ClearThenThrow,
    ReturnThenClockThrows,
}

sealed record CaseResult(string Case, string Expected, string Observed, bool SetupMatched, bool Matched,
    int LoadCount, bool? InvalidationRemovedEntry, bool ClearCalled, bool LoaderReturnedRefresh,
    int ClockFailureCount);

sealed class ScriptedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public Exception? NextFailure { get; set; }
    public int FailureCount { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            FailureCount++;
            throw failure;
        }

        return Now;
    }
}
