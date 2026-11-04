using System.Text.Json;

namespace DeploymentProfiles;

public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled);

public static class ProfileCodec
{
    public static ServiceProfile Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (root.TryGetProperty("version", out var version) && version.GetInt32() != 1))
                throw new FormatException("Unsupported profile.");

            var endpoint = root.GetProperty("endpoint").GetString();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
                (address.Scheme != "http" && address.Scheme != "https"))
                throw new FormatException("Invalid endpoint.");

            var timeoutMs = root.TryGetProperty("timeoutMs", out var timeout) ? timeout.GetInt32() : 5000;
            var retries = root.TryGetProperty("retries", out var attempts) ? attempts.GetInt32() : 0;
            var enabled = !root.TryGetProperty("enabled", out var flag) || flag.GetBoolean();
            if (timeoutMs is < 1 or > 60000 || retries is < 0 or > 5)
                throw new FormatException("Invalid transport settings.");

            return new ServiceProfile(endpoint!, timeoutMs, retries, enabled);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          KeyNotFoundException or ArgumentNullException)
        {
            throw new FormatException("Invalid profile.", exception);
        }
    }
}
