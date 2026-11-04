namespace ProductCatalog;

public sealed class CatalogUnavailableException(string message) : Exception(message);

public enum FreshnessPolicy
{
    RequireFresh,
    AllowStaleOnTransientFailure
}

public sealed record CatalogValue(string Value, bool IsStale);

public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    private readonly Dictionary<string, (string Value, DateTimeOffset LoadedAt)> _entries =
        new(StringComparer.Ordinal);

    public string Get(string key) => Read(key, FreshnessPolicy.RequireFresh).Value;

    public CatalogValue Read(string key, FreshnessPolicy policy)
    {
        if (policy is not (FreshnessPolicy.RequireFresh or FreshnessPolicy.AllowStaleOnTransientFailure))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown freshness policy.");

        ArgumentException.ThrowIfNullOrEmpty(key);
        var hasCurrent = _entries.TryGetValue(key, out var current);
        if (hasCurrent &&
            clock.GetUtcNow() - current.LoadedAt < TimeSpan.FromSeconds(30))
            return new(current.Value, false);

        string value;
        try
        {
            value = load(key);
        }
        catch (CatalogUnavailableException) when (
            policy == FreshnessPolicy.AllowStaleOnTransientFailure && hasCurrent &&
            clock.GetUtcNow() - current.LoadedAt < TimeSpan.FromSeconds(90))
        {
            return new(current.Value, true);
        }

        _entries[key] = (value, clock.GetUtcNow());
        return new(value, false);
    }

    public bool Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _entries.Remove(key);
    }

    public void Clear() => _entries.Clear();
}
