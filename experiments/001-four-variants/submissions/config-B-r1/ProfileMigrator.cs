using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Converts supported deployment profiles to a consistent version 2 JSON representation.</summary>
public static class ProfileMigrator
{
    /// <summary>Validates either profile format and writes compact version 2 JSON with explicit defaults.</summary>
    /// <param name="json">A version 1 or version 2 profile; an omitted version means version 1.</param>
    /// <returns>
    /// JSON ordered as version, transport (endpoint, timeoutMs), retry (count), and enabled.
    /// Unknown properties are omitted, and migrating the result produces the same string.
    /// </returns>
    /// <exception cref="FormatException">The input is null or is not a valid supported profile.</exception>
    public static string ToVersion2(string json)
    {
        // Parsing first shares all validation and default handling with the deployment adapter.
        var profile = ProfileCodec.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Explicit writes keep property order stable and include every value, even defaults.
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

        // Disposing the writer flushes its UTF-8 bytes before they are converted to the result string.
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
