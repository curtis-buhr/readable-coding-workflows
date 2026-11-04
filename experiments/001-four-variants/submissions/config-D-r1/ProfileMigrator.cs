using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Converts supported deployment profiles into a stable version 2 representation.</summary>
public static class ProfileMigrator
{
    /// <summary>Serializes a v1 or v2 profile as compact v2 JSON in a fixed property order.</summary>
    /// <param name="json">The profile JSON to validate and migrate.</param>
    /// <returns>
    /// JSON containing all recognized v2 settings, including defaults, without unknown fields.
    /// Migrating this output again produces the same string.
    /// </returns>
    /// <exception cref="FormatException">The input is not a valid supported profile.</exception>
    public static string ToVersion2(string json)
    {
        // Reuse parsing so migration applies exactly the same validation and defaults
        // as deployment requests, including validation of disabled profiles.
        var profile = ProfileCodec.Parse(json);
        using var buffer = new MemoryStream();

        // Explicit member writes fix the property order; the writer handles escaping
        // endpoint text and emits compact JSON with its default options.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);

            writer.WriteStartObject("transport");
            writer.WriteString("endpoint", profile.Endpoint);
            writer.WriteNumber("timeoutMs", profile.TimeoutMs);
            writer.WriteEndObject();

            writer.WriteStartObject("retry");
            writer.WriteNumber("count", profile.Retries);
            writer.WriteEndObject();
            writer.WriteBoolean("enabled", profile.Enabled);
            writer.WriteEndObject();
        }

        // Disposing the writer flushes its bytes before the buffer is decoded.
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
