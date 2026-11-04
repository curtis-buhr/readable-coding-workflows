namespace ProductCatalog;

public sealed class CatalogUnavailableException(string message) : Exception(message);

public sealed class CatalogCache(Func<string, string> load, TimeProvider clock)
{
    private readonly Dictionary<string, (string Value, DateTimeOffset LoadedAt)> _entries =
        new(StringComparer.Ordinal);

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

    public bool Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _entries.Remove(key);
    }

    public void Clear() => _entries.Clear();
}
