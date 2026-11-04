using System.Text.Json;

namespace DeploymentProfiles;

public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled)
{
#if REV2
    public int BackoffMs { get; init; }
#endif
}

public static class ProfileCodec
{
    public static ServiceProfile Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = ReadInt(root, "version", 1);
            if (version is not (1 or 2))
                throw new FormatException("Unsupported profile version.");

            var transport = version == 1 ? root : root.GetProperty("transport");
            var endpoint = transport.GetProperty("endpoint").GetString();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
                (address.Scheme != "http" && address.Scheme != "https"))
                throw new FormatException("Invalid endpoint.");
#if REV3
            if (endpoint!.Contains('?') || endpoint.Contains('#') || address.UserInfo.Length != 0)
                throw new FormatException("Endpoint cannot contain query, fragment, or user information.");
#endif

            var timeoutMs = ReadInt(transport, "timeoutMs", 5000);
            var retries = 0;
#if REV2
            var backoffMs = 0;
#endif
            if (version == 1)
                retries = ReadInt(root, "retries", 0);
            else if (root.TryGetProperty("retry", out var retry))
            {
                retries = ReadInt(retry, "count", 0);
#if REV2
                backoffMs = ReadInt(retry, "backoffMs", 0);
#endif
            }

            var enabled = !root.TryGetProperty("enabled", out var flag) || flag.GetBoolean();
            if (timeoutMs is < 1 or > 60000 || retries is < 0 or > 5)
                throw new FormatException("Invalid transport settings.");
#if REV2
            if (backoffMs is < 0 or > 30000)
                throw new FormatException("Invalid backoff.");
#endif
#if REV3
            if ((retries + 1) * timeoutMs + retries * backoffMs > 120000)
                throw new FormatException("Retry budget exceeded.");
#endif
            return new ServiceProfile(endpoint!, timeoutMs, retries, enabled)
#if REV2
                { BackoffMs = backoffMs }
#endif
                ;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentNullException)
        {
            throw new FormatException("Invalid profile.", error);
        }
    }

    private static int ReadInt(JsonElement owner, string name, int fallback) =>
        owner.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
}
