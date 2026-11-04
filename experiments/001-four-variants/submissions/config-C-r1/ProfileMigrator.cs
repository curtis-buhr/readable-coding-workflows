using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

public static class ProfileMigrator
{
    public static string ToVersion2(string json)
    {
        var profile = ProfileCodec.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
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

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
