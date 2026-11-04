namespace DeploymentProfiles;

/// <summary>Holds health request settings and retry backoff.</summary>
/// <param name="HealthUrl">The health request URL.</param>
/// <param name="Timeout">The request timeout.</param>
/// <param name="Retries">The number of retries.</param>
public sealed record DeploymentRequest(string HealthUrl, TimeSpan Timeout, int Retries)
{
    /// <summary>Gets the retry backoff in milliseconds, defaulting to zero.</summary>
    public int BackoffMs { get; init; }
}

/// <summary>Converts supported deployment profiles into health request settings.</summary>
public static class DeploymentAdapter
{
    /// <summary>Validates a profile and creates health request settings when it is enabled.</summary>
    /// <param name="json">The v1 or v2 profile JSON.</param>
    /// <returns>Request settings including retry backoff, or null for a valid disabled profile.</returns>
    /// <remarks>
    /// The health URL appends /health after trimming trailing / characters from the original endpoint.
    /// </remarks>
    /// <exception cref="FormatException">The input is not a valid supported profile, even if disabled.</exception>
    public static DeploymentRequest? Create(string json)
    {
        // Parse before checking enabled so disabled profiles receive the same validation.
        var profile = ProfileCodec.Parse(json);
        if (!profile.Enabled)
            return null;

        // Trim only trailing slashes before adding the health path so its separator
        // is unique and the original scheme, authority, and path spelling survive.
        var healthUrl = profile.Endpoint.TrimEnd('/') + "/health";
        return new DeploymentRequest(healthUrl,
            TimeSpan.FromMilliseconds(profile.TimeoutMs), profile.Retries)
        {
            BackoffMs = profile.BackoffMs
        };
    }
}
