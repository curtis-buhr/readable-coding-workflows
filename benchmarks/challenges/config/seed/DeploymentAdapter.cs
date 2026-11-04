namespace DeploymentProfiles;

public sealed record DeploymentRequest(string HealthUrl, TimeSpan Timeout, int Retries);

public static class DeploymentAdapter
{
    public static DeploymentRequest? Create(string json)
    {
        var profile = ProfileCodec.Parse(json);
        if (!profile.Enabled)
            return null;

        return new DeploymentRequest(profile.Endpoint + "/health",
            TimeSpan.FromMilliseconds(profile.TimeoutMs), profile.Retries);
    }
}
