namespace DeploymentProfiles;

/// <summary>Holds the health endpoint, timeout, and retry settings for a deployment.</summary>
/// <param name="HealthUrl">The health URL used for deployment checks.</param>
/// <param name="Timeout">The transport timeout.</param>
/// <param name="Retries">The retry count.</param>
public sealed record DeploymentRequest(string HealthUrl, TimeSpan Timeout, int Retries)
{
    /// <summary>Gets the retry backoff in milliseconds, defaulting to zero.</summary>
    public int BackoffMs { get; init; }
}

/// <summary>Creates deployment requests from either supported profile format.</summary>
public static class DeploymentAdapter
{
    /// <summary>Builds a request for an enabled profile by appending <c>/health</c> to its endpoint.</summary>
    /// <param name="json">A version 1 or version 2 profile, with version 1 assumed when omitted.</param>
    /// <returns>The request including retry backoff, or null when the validated profile is disabled.</returns>
    /// <exception cref="FormatException">The input is null or is not a valid supported profile.</exception>
    public static DeploymentRequest? Create(string json)
    {
        var profile = ProfileCodec.Parse(json);
        if (!profile.Enabled)
            return null;

        // Preserve literal concatenation, including any existing trailing slash, query, or fragment.
        return new DeploymentRequest(profile.Endpoint + "/health",
            TimeSpan.FromMilliseconds(profile.TimeoutMs), profile.Retries)
        {
            BackoffMs = profile.BackoffMs
        };
    }
}
