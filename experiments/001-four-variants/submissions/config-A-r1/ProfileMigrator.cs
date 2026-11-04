using System.Text.Json;

namespace DeploymentProfiles;

public static class ProfileMigrator
{
    public static string ToVersion2(string json)
    {
        var profile = ProfileCodec.Parse(json);
        return JsonSerializer.Serialize(new
        {
            version = 2,
            transport = new { endpoint = profile.Endpoint, timeoutMs = profile.TimeoutMs },
            retry = new { count = profile.Retries },
            enabled = profile.Enabled
        });
    }
}
