namespace ProductCatalog;

public sealed class CatalogUnavailableException(string message) : Exception(message);

/// <summary>Caches catalog descriptions and refreshes them once they reach 30 seconds of age.</summary>
/// <param name="load">The synchronous loader used for missing or expired descriptions.</param>
/// <param name="clock">The clock used to measure age from successful load completion.</param>
public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    private static readonly TimeSpan FreshLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleFallbackMaximumAge = TimeSpan.FromSeconds(90);

    private readonly Dictionary<string, (string Value, DateTimeOffset LoadedAt)> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Returns a fresh description, loading missing or expired data.</summary>
    /// <param name="key">The nonempty, case-sensitive key passed unchanged to the loader.</param>
    /// <returns>The cached or successfully loaded description.</returns>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    /// <remarks>Uses <see cref="FreshnessPolicy.RequireFresh"/>; loader failures propagate.</remarks>
    public string Get(string key) => Read(key, FreshnessPolicy.RequireFresh).Value;

    /// <summary>Reads a description, optionally allowing stale data after a transient refresh failure.</summary>
    /// <param name="key">The nonempty, case-sensitive key passed unchanged to the loader.</param>
    /// <param name="policy">Whether catalog unavailability may return eligible stale data.</param>
    /// <returns>Fresh data, or a stale fallback marked with <see cref="CatalogValue.IsStale"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The policy is undefined.</exception>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    /// <exception cref="CatalogUnavailableException">Loading fails without an eligible fallback.</exception>
    /// <remarks>
    /// Fresh entries are younger than 30 seconds. Successful loads reset age at completion.
    /// Only catalog unavailability allows fallback, and the retained entry must be younger than
    /// 90 seconds when loading fails. A fallback leaves age unchanged, so the next read retries.
    /// Other loader exceptions propagate. Policy validation precedes loading.
    /// </remarks>
    public CatalogValue Read(string key, FreshnessPolicy policy)
    {
        // Reject unsupported policies even when a cached value could satisfy this read.
        if (policy is not FreshnessPolicy.RequireFresh and not FreshnessPolicy.AllowStaleOnTransientFailure)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The freshness policy is undefined.");

        ArgumentException.ThrowIfNullOrEmpty(key);

        // Both policies use fresh data immediately; expired data always triggers a load.
        var hasCurrentEntry = _entries.TryGetValue(key, out var current);
        if (hasCurrentEntry && clock.GetUtcNow() - current.LoadedAt < FreshLifetime)
            return new CatalogValue(current.Value, false);

        // Check fallback age after loading fails, since loading can take time. The entry must
        // also remain cached because a loader can invalidate or clear it before throwing.
        string refreshedValue;
        try
        {
            refreshedValue = load(key);
        }
        catch (CatalogUnavailableException) when (
            policy == FreshnessPolicy.AllowStaleOnTransientFailure &&
            hasCurrentEntry &&
            _entries.TryGetValue(key, out var retainedEntry) && retainedEntry == current &&
            clock.GetUtcNow() - current.LoadedAt < StaleFallbackMaximumAge)
        {
            // Leave the timestamp untouched so the next read attempts another refresh.
            return new CatalogValue(current.Value, true);
        }

        // Timestamp after loading so the full freshness interval starts at completion.
        _entries[key] = (refreshedValue, clock.GetUtcNow());
        return new CatalogValue(refreshedValue, false);
    }

    /// <summary>Removes the cached description for a key, including any stale fallback.</summary>
    /// <param name="key">The nonempty, case-sensitive key to remove.</param>
    /// <returns>Whether an entry was removed.</returns>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public bool Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _entries.Remove(key);
    }

    /// <summary>Removes every cached description, including all stale fallbacks.</summary>
    public void Clear() => _entries.Clear();
}
