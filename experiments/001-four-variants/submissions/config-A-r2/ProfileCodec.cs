using System.Text.Json;

namespace DeploymentProfiles;

public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled)
{
    public int BackoffMs { get; init; }
}

public static class ProfileCodec
{
    public static ServiceProfile Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Invalid profile.");

            var versionNumber = root.TryGetProperty("version", out var version) ? version.GetInt32() : 1;
            if (versionNumber is not (1 or 2))
                throw new FormatException("Unsupported profile.");

            var transport = versionNumber == 2 ? root.GetProperty("transport") : root;
            if (transport.ValueKind != JsonValueKind.Object)
                throw new FormatException("Invalid transport settings.");

            var endpoint = transport.GetProperty("endpoint").GetString();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
                (address.Scheme != "http" && address.Scheme != "https"))
                throw new FormatException("Invalid endpoint.");

            var timeoutMs = transport.TryGetProperty("timeoutMs", out var timeout) ? timeout.GetInt32() : 5000;
            var retries = 0;
            var backoffMs = 0;
            if (versionNumber == 1)
            {
                retries = root.TryGetProperty("retries", out var attempts) ? attempts.GetInt32() : 0;
            }
            else if (root.TryGetProperty("retry", out var retry))
            {
                if (retry.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Invalid retry settings.");

                retries = retry.TryGetProperty("count", out var count) ? count.GetInt32() : 0;
                backoffMs = retry.TryGetProperty("backoffMs", out var backoff) ? backoff.GetInt32() : 0;
            }

            var enabled = !root.TryGetProperty("enabled", out var flag) || flag.GetBoolean();
            if (timeoutMs is < 1 or > 60000 || retries is < 0 or > 5 || backoffMs is < 0 or > 30000)
                throw new FormatException("Invalid transport settings.");

            return new ServiceProfile(endpoint!, timeoutMs, retries, enabled) { BackoffMs = backoffMs };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          KeyNotFoundException or ArgumentNullException)
        {
            throw new FormatException("Invalid profile.", exception);
        }
    }
}
