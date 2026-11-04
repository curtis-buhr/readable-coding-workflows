using System.Collections;
using System.Globalization;
using System.Text.Json;
using ProductCatalog;

var cases = new List<CaseResult>();

void Run(string id, int introduced, Action check)
{
    try
    {
        check();
        cases.Add(new CaseResult(id, introduced, true, null));
    }
    catch (Exception error)
    {
        cases.Add(new CaseResult(id, introduced, false, $"{error.GetType().Name}: {error.Message}"));
    }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

Run("cache.strict-30-second-boundary", 0, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => (++calls).ToString(), clock);
    Equal("1", cache.Get("SKU"));
    clock.Advance(29.999);
    Equal("1", cache.Get("SKU"));
    clock.Advance(0.001);
    Equal("2", cache.Get("SKU"));
    Equal(2, calls);
});
Run("cache.expiration-from-load-completion", 0, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ =>
    {
        clock.Advance(5);
        return (++calls).ToString();
    }, clock);
    Equal("1", cache.Get("SKU"));
    clock.Advance(29);
    Equal("1", cache.Get("SKU"));
    clock.Advance(1);
    Equal("2", cache.Get("SKU"));
});
Run("cache.invalidate-and-clear", 0, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => (++calls).ToString(), clock);
    Equal(false, cache.Invalidate("SKU"));
    Equal("1", cache.Get("SKU"));
    Equal(true, cache.Invalidate("SKU"));
    Equal(false, cache.Invalidate("SKU"));
    Equal("2", cache.Get("SKU"));
    Equal("3", cache.Get("OTHER"));
    cache.Clear();
    Equal("4", cache.Get("SKU"));
    Equal("5", cache.Get("OTHER"));
});
Run("cache.failure-does-not-refresh", 0, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => ++calls == 2 ? throw new CatalogUnavailableException("transient") : calls.ToString(), clock);
    Equal("1", cache.Get("SKU"));
    clock.Advance(30);
    Throws<CatalogUnavailableException>(() => cache.Get("SKU"));
    Equal("3", cache.Get("SKU"));
});
Run("cache.reject-null-and-empty", 0, () =>
{
    var cache = new CatalogCache(_ => throw new InvalidOperationException("Unexpected load."), new ManualClock());
    Throws<ArgumentNullException>(() => cache.Get(null!));
    Throws<ArgumentException>(() => cache.Get(""));
    Throws<ArgumentNullException>(() => cache.Invalidate(null!));
    Throws<ArgumentException>(() => cache.Invalidate(""));
});
Run("reader.preserves-spelling-and-trims-lookup", 0, () =>
{
    var cache = new CatalogCache(key => key, new ManualClock());
    Equal(new ProductCard(" SKU  ", "SKU"), new ProductCardReader(cache).Read(" SKU  "));
});

#if REV1
Run("batch.order-duplicates-and-one-enumeration", 1, () =>
{
    var loads = new List<string>();
    var reader = new ProductCardReader(new CatalogCache(key =>
    {
        loads.Add(key);
        return $"value-{key}";
    }, new ManualClock()));
    var results = reader.ReadMany(new OneShot<string?>([" X ", "Y", "X", null, "", " ", " Y "]));
    Equal("X,Y", string.Join(",", loads));
    Equal(7, results.Count);
    Equal(new CardReadResult(" X ", new(" X ", "value-X"), null), results[0]);
    Equal(new CardReadResult("Y", new("Y", "value-Y"), null), results[1]);
    Equal(new CardReadResult("X", new("X", "value-X"), null), results[2]);
    Equal(new CardReadResult(null, null, "invalid-key"), results[3]);
    Equal(new CardReadResult("", null, "invalid-key"), results[4]);
    Equal(new CardReadResult(" ", null, "invalid-key"), results[5]);
    Equal(new CardReadResult(" Y ", new(" Y ", "value-Y"), null), results[6]);
});
Run("batch.unavailable-shared-and-next-call-retries", 1, () =>
{
    var attempts = 0;
    var reader = new ProductCardReader(new CatalogCache(key =>
    {
        if (key == "X" && ++attempts == 1)
            throw new CatalogUnavailableException("temporary");
        return key;
    }, new ManualClock()));
    var results = reader.ReadMany(["X", "Y", " X "]);
    Equal(new CardReadResult("X", null, "unavailable"), results[0]);
    Equal(new CardReadResult("Y", new("Y", "Y"), null), results[1]);
    Equal(new CardReadResult(" X ", null, "unavailable"), results[2]);
    Equal(1, attempts);
    Equal(new CardReadResult("X", new("X", "X"), null), reader.ReadMany(["X"]).Single());
    Equal(2, attempts);
});
Run("batch.unexpected-errors-propagate", 1, () =>
{
    var reader = new ProductCardReader(new CatalogCache(_ => throw new InvalidOperationException("data corrupt"), new ManualClock()));
    Throws<InvalidOperationException>(() => reader.ReadMany(["X"]));
    Throws<ArgumentNullException>(() => reader.ReadMany(null!));
});
#if !REV3
Run("batch.original-ordinal-case-sensitive", 1, () =>
{
    var reader = new ProductCardReader(new CatalogCache(key => key, new ManualClock()));
    var results = reader.ReadMany(["ab", " AB "]);
    Equal("ab", results[0].Card!.Description);
    Equal("AB", results[1].Card!.Description);
});
#endif
#endif

#if REV2
const FreshnessPolicy stale = FreshnessPolicy.AllowStaleOnTransientFailure;
const FreshnessPolicy strict = FreshnessPolicy.RequireFresh;
Run("freshness.success-and-strict-default", 2, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => ++calls <= 2 ? calls.ToString() : throw new CatalogUnavailableException("offline"), clock);
    Equal(new CatalogValue("1", false), cache.Read("X", stale));
    clock.Advance(29);
    Equal(new CatalogValue("1", false), cache.Read("X", strict));
    Equal(new CatalogValue("1", false), cache.Read("X", stale));
    Equal(1, calls);
    clock.Advance(1);
    Equal(new CatalogValue("2", false), cache.Read("X", stale));
    clock.Advance(29);
    Equal(new CatalogValue("2", false), cache.Read("X", stale));
    clock.Advance(1);
    Throws<CatalogUnavailableException>(() => cache.Get("X"));
    Throws<CatalogUnavailableException>(() => new ProductCardReader(cache).Read("X"));
});
Run("freshness.stale-fallback-no-age-reset", 2, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => ++calls == 1 ? "old" : throw new CatalogUnavailableException("offline"), clock);
    cache.Get("X");
    clock.Advance(30);
    Equal(new CatalogValue("old", true), cache.Read("X", stale));
    Equal(new CatalogValue("old", true), cache.Read("X", stale));
    Equal(3, calls);
    clock.Advance(59.999);
    Equal(new CatalogValue("old", true), cache.Read("X", stale));
    clock.Advance(0.001);
    Throws<CatalogUnavailableException>(() => cache.Read("X", stale));
    Equal(5, calls);
});
Run("freshness.age-checked-when-loader-throws", 2, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ =>
    {
        if (++calls == 1)
            return "old";
        clock.Advance(3);
        throw new CatalogUnavailableException("late failure");
    }, clock);
    cache.Get("X");
    clock.Advance(88);
    Throws<CatalogUnavailableException>(() => cache.Read("X", stale));
});
Run("freshness.only-transient-errors-are-eligible", 2, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => ++calls == 1 ? "old" : throw new InvalidOperationException("permanent"), clock);
    cache.Get("X");
    clock.Advance(30);
    Throws<InvalidOperationException>(() => cache.Read("X", stale));
});
Run("freshness.missing-invalidated-and-cleared", 2, () =>
{
    var clock = new ManualClock();
    var canLoad = false;
    var cache = new CatalogCache(key => canLoad ? key : throw new CatalogUnavailableException("offline"), clock);
    Throws<CatalogUnavailableException>(() => cache.Read("X", stale));
    canLoad = true;
    cache.Get("X");
    cache.Get("Y");
    clock.Advance(45);
    canLoad = false;
    Equal(true, cache.Invalidate("X"));
    Throws<CatalogUnavailableException>(() => cache.Read("X", stale));
    cache.Clear();
    Throws<CatalogUnavailableException>(() => cache.Read("Y", stale));
});
Run("freshness.invalid-policy-before-work", 2, () =>
{
    var cache = new CatalogCache(_ => throw new InvalidOperationException("Unexpected load."), new ManualClock());
    Throws<ArgumentOutOfRangeException>(() => cache.Read("X", (FreshnessPolicy)57));
    var reader = new ProductCardReader(cache);
    Throws<ArgumentOutOfRangeException>(() => reader.ReadMany(new OneShot<string?>([], rejectFirst: true), (FreshnessPolicy)57));
});
Run("batch.freshness-fallback-and-default", 2, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(_ => ++calls == 1 ? "original" : throw new CatalogUnavailableException("offline"), clock);
    var reader = new ProductCardReader(cache);
    cache.Get("X");
    clock.Advance(45);
    var strictResult = reader.ReadMany(["X", " X "]);
    Equal("unavailable", strictResult[0].Error);
    Equal("unavailable", strictResult[1].Error);
    Equal(false, strictResult[0].IsStale);
    Equal(2, calls);
    var staleResult = reader.ReadMany(["X", " X ", " ", "Y"], stale);
    Equal(new CardReadResult("X", new("X", "original"), null) { IsStale = true }, staleResult[0]);
    Equal(new CardReadResult(" X ", new(" X ", "original"), null) { IsStale = true }, staleResult[1]);
    Equal(new CardReadResult(" ", null, "invalid-key"), staleResult[2]);
    Equal(new CardReadResult("Y", null, "unavailable"), staleResult[3]);
    var (key, card, error) = staleResult[0];
    Equal("X", key);
    Equal("original", card!.Description);
    Equal<string?>(null, error);
    Equal(4, calls);
});
#endif

#if REV3
Run("identity.direct-cache-and-invalidation", 3, () =>
{
    var keys = new List<string>();
    var cache = new CatalogCache(key => { keys.Add(key); return key; }, new ManualClock());
    Equal("MIXED-ID", cache.Get(" mixed-ID "));
    Equal(new CatalogValue("MIXED-ID", false), cache.Read("MiXeD-iD", strict));
    Equal(1, keys.Count);
    Equal(true, cache.Invalidate("  mixed-id "));
    Equal("MIXED-ID", cache.Get("MIXED-ID"));
    Equal("MIXED-ID,MIXED-ID", string.Join(",", keys));
});
Run("identity.invalid-keys-across-entry-points", 3, () =>
{
    var cache = new CatalogCache(_ => throw new InvalidOperationException("Unexpected load."), new ManualClock());
    var reader = new ProductCardReader(cache);
    Throws<ArgumentNullException>(() => reader.Read(null!));
    Throws<ArgumentNullException>(() => cache.Read(null!, stale));
    foreach (var key in new[] { "", " ", "\t\r\n" })
    {
        Throws<ArgumentException>(() => cache.Get(key));
        Throws<ArgumentException>(() => cache.Read(key, stale));
        Throws<ArgumentException>(() => cache.Invalidate(key));
        Throws<ArgumentException>(() => reader.Read(key));
    }
});
Run("identity.invariant-and-original-card-spelling", 3, () =>
{
    var previousCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        var cache = new CatalogCache(key => key, new ManualClock());
        var reader = new ProductCardReader(cache);
        Equal(new ProductCard(" sku-i ", "SKU-I"), reader.Read(" sku-i "));
        Equal("SKU-I", cache.Get("SKU-I"));
    }
    finally
    {
        CultureInfo.CurrentCulture = previousCulture;
    }
});
Run("identity.batch-duplicates-for-both-policies", 3, () =>
{
    var clock = new ManualClock();
    var calls = 0;
    var cache = new CatalogCache(key => ++calls == 1 ? key : throw new CatalogUnavailableException("offline"), clock);
    var reader = new ProductCardReader(cache);
    cache.Get(" sku ");
    clock.Advance(40);
    var strictResults = reader.ReadMany(["sKu", " SKU "]);
    Equal(new CardReadResult("sKu", null, "unavailable"), strictResults[0]);
    Equal(new CardReadResult(" SKU ", null, "unavailable"), strictResults[1]);
    Equal(2, calls);
    var staleResults = reader.ReadMany([" SKU ", "sKu"], stale);
    Equal(new CardReadResult(" SKU ", new(" SKU ", "SKU"), null) { IsStale = true }, staleResults[0]);
    Equal(new CardReadResult("sKu", new("sKu", "SKU"), null) { IsStale = true }, staleResults[1]);
    Equal(3, calls);
});
#endif

Console.Write(JsonSerializer.Serialize(cases, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

sealed record CaseResult(string Id, int Introduced, bool Passed, string? Detail);

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(double seconds) => _now = _now.Add(TimeSpan.FromSeconds(seconds));
}

sealed class OneShot<T>(IEnumerable<T> items, bool rejectFirst = false) : IEnumerable<T>
{
    private bool _used;
    public IEnumerator<T> GetEnumerator()
    {
        if (_used || rejectFirst)
            throw new InvalidOperationException("Unexpected enumeration.");
        _used = true;
        return items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
