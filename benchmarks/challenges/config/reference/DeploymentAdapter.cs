namespace DeploymentProfiles;

public sealed record DeploymentRequest(string HealthUrl, TimeSpan Timeout, int Retries)
{
#if REV2
    public int BackoffMs { get; init; }
#endif
}

public static class DeploymentAdapter
{
    public static DeploymentRequest? Create(string json)
    {
        var profile = ProfileCodec.Parse(json);
        if (!profile.Enabled)
            return null;

#if REV3
        var healthUrl = profile.Endpoint.TrimEnd('/') + "/health";
#else
        var healthUrl = profile.Endpoint + "/health";
#endif
        return new DeploymentRequest(healthUrl, TimeSpan.FromMilliseconds(profile.TimeoutMs), profile.Retries)
#if REV2
            { BackoffMs = profile.BackoffMs }
#endif
            ;
    }
}
