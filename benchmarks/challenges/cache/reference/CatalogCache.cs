namespace ProductCatalog;

public sealed class CatalogUnavailableException(string message) : Exception(message);

#if REV2
public enum FreshnessPolicy { RequireFresh, AllowStaleOnTransientFailure }
public sealed record CatalogValue(string Value, bool IsStale);
#endif

public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    private readonly Dictionary<string, (string Value, DateTimeOffset LoadedAt)> _entries =
        new(StringComparer.Ordinal);

#if REV2
    public string Get(string key) => Read(key, FreshnessPolicy.RequireFresh).Value;

    public CatalogValue Read(string key, FreshnessPolicy policy)
    {
        if (policy is not (FreshnessPolicy.RequireFresh or FreshnessPolicy.AllowStaleOnTransientFailure))
            throw new ArgumentOutOfRangeException(nameof(policy));
#if REV3
        key = NormalizeKey(key);
#else
        ArgumentException.ThrowIfNullOrEmpty(key);
#endif
        var hasPrior = _entries.TryGetValue(key, out var current);
        if (hasPrior && clock.GetUtcNow() - current.LoadedAt < TimeSpan.FromSeconds(30))
            return new CatalogValue(current.Value, false);

        try
        {
            var value = load(key);
            _entries[key] = (value, clock.GetUtcNow());
            return new CatalogValue(value, false);
        }
        catch (CatalogUnavailableException) when (hasPrior &&
            policy == FreshnessPolicy.AllowStaleOnTransientFailure &&
            clock.GetUtcNow() - current.LoadedAt < TimeSpan.FromSeconds(90))
        {
            return new CatalogValue(current.Value, true);
        }
    }
#else
    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_entries.TryGetValue(key, out var current) &&
            clock.GetUtcNow() - current.LoadedAt < TimeSpan.FromSeconds(30))
            return current.Value;

        var value = load(key);
        _entries[key] = (value, clock.GetUtcNow());
        return value;
    }
#endif

    public bool Invalidate(string key)
    {
#if REV3
        key = NormalizeKey(key);
#else
        ArgumentException.ThrowIfNullOrEmpty(key);
#endif
        return _entries.Remove(key);
    }

    public void Clear() => _entries.Clear();

#if REV3
    internal static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim().ToUpperInvariant();
    }
#endif
}
