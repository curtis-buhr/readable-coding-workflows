namespace ProductCatalog;

/// <summary>Controls whether a transient refresh failure may return a retained catalog entry.</summary>
public enum FreshnessPolicy
{
    /// <summary>Propagates refresh failures instead of returning expired data.</summary>
    RequireFresh,

    /// <summary>Allows a retained entry younger than 90 seconds when a refresh is unavailable.</summary>
    AllowStaleOnTransientFailure
}

/// <summary>Contains a catalog description and whether it was returned after a failed refresh.</summary>
/// <param name="Value">The cached or newly loaded description.</param>
/// <param name="IsStale">True only when an expired entry was used after a transient refresh failure.</param>
public sealed record CatalogValue(string Value, bool IsStale);

/// <summary>Signals a transient failure to load catalog data.</summary>
/// <param name="message">The description of the catalog outage.</param>
public sealed class CatalogUnavailableException(string message) : Exception(message);

/// <summary>Caches catalog descriptions for 30 seconds, with optional stale fallback on outages.</summary>
/// <param name="load">Loads a description for a key; successful loads replace the cached entry.</param>
/// <param name="clock">Supplies the time used to measure age from successful load completion.</param>
/// <remarks>Concurrent access is not supported.</remarks>
public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    // Both windows start at successful load completion; serving stale data never extends either window.
    private static readonly TimeSpan FreshLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleFallbackMaximumAge = TimeSpan.FromSeconds(90);
    private readonly Dictionary<string, (string Value, DateTimeOffset LoadCompletedAt)> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Gets a fresh description, loading it when missing or expired.</summary>
    /// <param name="key">The nonempty, case-sensitive cache key.</param>
    /// <returns>The cached or successfully refreshed description.</returns>
    /// <remarks>Equivalent to reading with <see cref="FreshnessPolicy.RequireFresh"/>; loader exceptions propagate.</remarks>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public string Get(string key) => Read(key, FreshnessPolicy.RequireFresh).Value;

    /// <summary>Reads a description, optionally reusing expired data during a transient refresh outage.</summary>
    /// <param name="key">The nonempty, case-sensitive cache key.</param>
    /// <param name="policy">Controls whether a failed refresh may return stale data.</param>
    /// <returns>
    /// Fresh data when younger than 30 seconds or successfully refreshed. With stale fallback enabled,
    /// a retained entry younger than 90 seconds at loader failure may be returned with IsStale true.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The policy is undefined; the loader is not called.</exception>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    /// <remarks>
    /// Only CatalogUnavailableException permits fallback. Other loader exceptions propagate.
    /// Successful loads reset the age at completion; fallback preserves the entry's age and retries next time.
    /// </remarks>
    public CatalogValue Read(string key, FreshnessPolicy policy)
    {
        // Validate before any lookup so an unknown policy cannot be hidden by a cache hit.
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown freshness policy.");
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Capture the previous entry for fallback; fresh entries satisfy either policy immediately.
        var hasCurrent = _entries.TryGetValue(key, out var current);
        if (hasCurrent && clock.GetUtcNow() - current.LoadCompletedAt < FreshLifetime)
            return new CatalogValue(current.Value, false);

        // Limit fallback to loader outages, checking age after the failed attempt has consumed time.
        string refreshedValue;
        try
        {
            refreshedValue = load(key);
        }
        catch (CatalogUnavailableException) when (
            policy == FreshnessPolicy.AllowStaleOnTransientFailure && hasCurrent &&
            _entries.TryGetValue(key, out var retained) && retained == current &&
            clock.GetUtcNow() - current.LoadCompletedAt < StaleFallbackMaximumAge)
        {
            // Require the entry to remain cached, so invalidation or clearing during loading prevents reuse.
            return new CatalogValue(current.Value, true);
        }

        // A slow successful load still receives its full freshness window, measured from completion.
        _entries[key] = (refreshedValue, clock.GetUtcNow());
        return new CatalogValue(refreshedValue, false);
    }

    /// <summary>Removes a key's entry so subsequent reads cannot reuse it, including as stale data.</summary>
    /// <param name="key">The nonempty, case-sensitive cache key to remove.</param>
    /// <returns>True if an entry was removed; otherwise false.</returns>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public bool Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _entries.Remove(key);
    }

    /// <summary>Removes all entries, making their values unavailable for fresh or stale reads.</summary>
    public void Clear() => _entries.Clear();
}
