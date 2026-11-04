namespace ProductCatalog;

public sealed class CatalogUnavailableException(string message) : Exception(message);

/// <summary>Caches descriptions by canonical key and refreshes them once they reach 30 seconds of age.</summary>
/// <param name="load">The synchronous loader called with a trimmed, invariant-uppercase key for missing or expired descriptions.</param>
/// <param name="clock">The clock used to measure age from successful load completion.</param>
public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    private static readonly TimeSpan FreshLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleFallbackMaximumAge = TimeSpan.FromSeconds(90);

    private readonly Dictionary<string, (string Value, DateTimeOffset LoadedAt)> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Returns a fresh description, loading missing or expired data.</summary>
    /// <param name="key">The key, trimmed and converted to uppercase invariantly for cache identity and loading.</param>
    /// <returns>The cached or successfully loaded description.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentException">The key is empty or whitespace-only.</exception>
    /// <remarks>Uses <see cref="FreshnessPolicy.RequireFresh"/>; loader failures propagate.</remarks>
    public string Get(string key) => Read(key, FreshnessPolicy.RequireFresh).Value;

    /// <summary>Reads a description, optionally allowing stale data after a transient refresh failure.</summary>
    /// <param name="key">The key, trimmed and converted to uppercase invariantly for cache identity and loading.</param>
    /// <param name="policy">Whether catalog unavailability may return eligible stale data.</param>
    /// <returns>Fresh data, or a stale fallback marked with <see cref="CatalogValue.IsStale"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The policy is undefined.</exception>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentException">The key is empty or whitespace-only.</exception>
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

        // Use one identity for lookup, upstream loading, and retention checks after failure.
        var canonicalKey = CanonicalizeKey(key);

        // Both policies use fresh data immediately; expired data always triggers a load.
        var hasCurrentEntry = _entries.TryGetValue(canonicalKey, out var current);
        if (hasCurrentEntry && clock.GetUtcNow() - current.LoadedAt < FreshLifetime)
            return new CatalogValue(current.Value, false);

        // Check fallback age after loading fails, since loading can take time. The entry must
        // also remain cached because a loader can invalidate or clear it before throwing.
        string refreshedValue;
        try
        {
            refreshedValue = load(canonicalKey);
        }
        catch (CatalogUnavailableException) when (
            policy == FreshnessPolicy.AllowStaleOnTransientFailure &&
            hasCurrentEntry &&
            _entries.TryGetValue(canonicalKey, out var retainedEntry) && retainedEntry == current &&
            clock.GetUtcNow() - current.LoadedAt < StaleFallbackMaximumAge)
        {
            // Leave the timestamp untouched so the next read attempts another refresh.
            return new CatalogValue(current.Value, true);
        }

        // Timestamp after loading so the full freshness interval starts at completion.
        _entries[canonicalKey] = (refreshedValue, clock.GetUtcNow());
        return new CatalogValue(refreshedValue, false);
    }

    /// <summary>Removes the cached description for a key, including any stale fallback.</summary>
    /// <param name="key">The key, trimmed and converted to uppercase invariantly to identify the entry to remove.</param>
    /// <returns>Whether an entry was removed.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentException">The key is empty or whitespace-only.</exception>
    public bool Invalidate(string key) => _entries.Remove(CanonicalizeKey(key));

    /// <summary>Removes every cached description, including all stale fallbacks.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Validates a requested key and creates its shared cache and reader identity.</summary>
    /// <param name="key">The requested catalog key.</param>
    /// <returns>The trimmed key converted to uppercase using invariant culture.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentException">The key is empty or whitespace-only.</exception>
    internal static string CanonicalizeKey(string key)
    {
        // Invariant casing makes ordinal identity independent of the caller's current culture.
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim().ToUpperInvariant();
    }
}
