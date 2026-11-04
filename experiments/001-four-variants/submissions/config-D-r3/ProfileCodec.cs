using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Holds deployment settings and retry backoff without changing the supplied endpoint text.</summary>
/// <param name="Endpoint">The endpoint string.</param>
/// <param name="TimeoutMs">The timeout in milliseconds.</param>
/// <param name="Retries">The number of retries.</param>
/// <param name="Enabled">Whether to create a deployment request for this profile.</param>
public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled)
{
    /// <summary>Gets the retry backoff in milliseconds, defaulting to zero.</summary>
    public int BackoffMs { get; init; }
}

/// <summary>Parses supported deployment profile formats into shared settings.</summary>
public static class ProfileCodec
{
    /// <summary>Reads a v1 or v2 profile, treating an absent version as v1.</summary>
    /// <param name="json">The profile JSON with case-sensitive property names.</param>
    /// <returns>
    /// Validated settings with defaults applied and endpoint text preserved.
    /// Endpoints are absolute HTTP(S) URIs with no query, fragment, or user information.
    /// Retry backoff defaults to zero and is read only from the v2 retry object.
    /// The total worst-case retry duration is at most 120000 milliseconds, even when disabled.
    /// </returns>
    /// <exception cref="FormatException">
    /// The input is null, malformed, unsupported, or contains invalid recognized settings.
    /// </exception>
    public static ServiceProfile Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The profile must be an object.");

            // Select the version's transport location before reading settings so unrelated
            // fields from the other format remain unknown and are ignored.
            var version = ReadOptionalInt32(root, "version", 1);
            var transport = root;
            if (version == 2)
            {
                if (!root.TryGetProperty("transport", out transport) ||
                    transport.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Version 2 requires a transport object.");
            }
            else if (version != 1)
                throw new FormatException("Unsupported profile version.");

            // Both layouts share defaults and bounds after their fields are located.
            var endpoint = ReadEndpoint(transport);
            var timeoutMs = ReadOptionalInt32(transport, "timeoutMs", 5000);
            var (retries, backoffMs) = ReadRetrySettings(root, version);
            var enabled = ReadEnabled(root);
            if (timeoutMs is < 1 or > 60000 || retries is < 0 or > 5 ||
                backoffMs is < 0 or > 30000)
                throw new FormatException("Invalid transport settings.");

            // Every attempt consumes a timeout, while only retries add backoff.
            // The individual bounds above keep this calculation within Int32.
            var worstCaseRetryDurationMs = (retries + 1) * timeoutMs + retries * backoffMs;
            if (worstCaseRetryDurationMs > 120000)
                throw new FormatException("The total retry budget exceeds 120000 milliseconds.");

            // A body property carries backoff while preserving positional construction
            // and deconstruction for callers of the original record API.
            return new ServiceProfile(endpoint, timeoutMs, retries, enabled)
            {
                BackoffMs = backoffMs
            };
        }
        // JSON strings can fail during transcoding or deferred decoding as well as parsing.
        // Normalize these failures and null inputs to the public exception contract.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          ArgumentException)
        {
            throw new FormatException("Invalid profile.", exception);
        }
    }

    /// <summary>Reads an absolute HTTP(S) endpoint without query, fragment, or user information.</summary>
    /// <param name="transport">The object containing the required endpoint string.</param>
    /// <returns>The original endpoint text, including its path and trailing slashes.</returns>
    /// <exception cref="FormatException">The endpoint is missing, has the wrong type, or is invalid.</exception>
    private static string ReadEndpoint(JsonElement transport)
    {
        if (!transport.TryGetProperty("endpoint", out var endpointElement) ||
            endpointElement.ValueKind != JsonValueKind.String)
            throw new FormatException("The endpoint must be a string.");

        // Use Uri for validation while retaining the original text, since URI
        // normalization could change scheme, authority, or path spelling.
        var endpoint = endpointElement.GetString()!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new FormatException("Invalid endpoint.");

        // Raw delimiters reveal empty query and fragment components. KeepDelimiter
        // retains an empty user-info marker and distinguishes authority from path text.
        if (endpoint.Contains('?') || endpoint.Contains('#') ||
            address.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter,
                UriFormat.UriEscaped).Length > 0)
            throw new FormatException("The endpoint must not contain a query, fragment, or user information.");

        return endpoint;
    }

    /// <summary>Reads an optional Int32 number, using the fallback only when the field is absent.</summary>
    /// <exception cref="FormatException">A present field is not an Int32 JSON number.</exception>
    private static int ReadOptionalInt32(JsonElement container, string propertyName, int fallback)
    {
        if (!container.TryGetProperty(propertyName, out var value))
            return fallback;

        // Check the kind first because TryGetInt32 throws for non-number JSON values.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new FormatException($"The {propertyName} field must be an Int32 number.");

        return number;
    }

    /// <summary>Reads retry count and backoff milliseconds, defaulting omitted values to zero.</summary>
    /// <param name="root">The profile object for the already validated version.</param>
    /// <param name="version">The supported profile version, either 1 or 2.</param>
    /// <returns>The retry count and backoff milliseconds. Version 1 always has zero backoff.</returns>
    /// <exception cref="FormatException">
    /// The retry container is not an object or a supplied retry value is not an Int32 number.
    /// </exception>
    private static (int Count, int BackoffMs) ReadRetrySettings(JsonElement root, int version)
    {
        // Keep v1's flat settings isolated so any backoff fields there remain ignored.
        if (version == 1)
            return (ReadOptionalInt32(root, "retries", 0), 0);

        // Missing retry settings use zero defaults. A supplied container must be an
        // object so explicit null and scalars cannot bypass integer validation.
        if (!root.TryGetProperty("retry", out var retry))
            return (0, 0);
        if (retry.ValueKind != JsonValueKind.Object)
            throw new FormatException("The retry field must be an object.");

        return (ReadOptionalInt32(retry, "count", 0),
            ReadOptionalInt32(retry, "backoffMs", 0));
    }

    /// <summary>Reads the shared enabled flag, defaulting an absent flag to true.</summary>
    /// <exception cref="FormatException">A present enabled field is not a JSON boolean.</exception>
    private static bool ReadEnabled(JsonElement root)
    {
        if (!root.TryGetProperty("enabled", out var enabled))
            return true;

        // Explicit null is a supplied value, so it must not take the missing-field default.
        if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("The enabled field must be a boolean.");

        return enabled.GetBoolean();
    }
}
