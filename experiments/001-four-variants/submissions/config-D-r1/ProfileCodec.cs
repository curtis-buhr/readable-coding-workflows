using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Holds deployment settings without changing the supplied endpoint text.</summary>
/// <param name="Endpoint">The endpoint string.</param>
/// <param name="TimeoutMs">The timeout in milliseconds.</param>
/// <param name="Retries">The number of retries.</param>
/// <param name="Enabled">Whether to create a deployment request for this profile.</param>
public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled);

/// <summary>Parses supported deployment profile formats into shared settings.</summary>
public static class ProfileCodec
{
    /// <summary>Reads a v1 or v2 profile, treating an absent version as v1.</summary>
    /// <param name="json">The profile JSON; property names are case-sensitive.</param>
    /// <returns>Validated settings with defaults applied and endpoint text preserved.</returns>
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
            var retries = ReadRetryCount(root, version);
            var enabled = ReadEnabled(root);
            if (timeoutMs is < 1 or > 60000 || retries is < 0 or > 5)
                throw new FormatException("Invalid transport settings.");

            return new ServiceProfile(endpoint, timeoutMs, retries, enabled);
        }
        // JSON strings can fail during transcoding or deferred decoding as well as parsing.
        // Normalize these failures and null inputs to the public exception contract.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          ArgumentException)
        {
            throw new FormatException("Invalid profile.", exception);
        }
    }

    /// <summary>Reads a required absolute HTTP or HTTPS endpoint without normalizing its text.</summary>
    /// <exception cref="FormatException">The endpoint is missing, has the wrong type, or is invalid.</exception>
    private static string ReadEndpoint(JsonElement transport)
    {
        if (!transport.TryGetProperty("endpoint", out var endpointElement) ||
            endpointElement.ValueKind != JsonValueKind.String)
            throw new FormatException("The endpoint must be a string.");

        // Uri is used only for validation: its normalized representation could change
        // the endpoint text consumed by the existing deployment adapter.
        var endpoint = endpointElement.GetString()!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new FormatException("Invalid endpoint.");

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

    /// <summary>Reads the version-specific retry setting, defaulting omitted retry values to zero.</summary>
    /// <exception cref="FormatException">The retry container or count has an invalid type.</exception>
    private static int ReadRetryCount(JsonElement root, int version)
    {
        if (version == 1)
            return ReadOptionalInt32(root, "retries", 0);

        // An omitted retry object uses the default, while an explicit null or scalar
        // is invalid even though a missing count inside an object is allowed.
        if (!root.TryGetProperty("retry", out var retry))
            return 0;
        if (retry.ValueKind != JsonValueKind.Object)
            throw new FormatException("The retry field must be an object.");

        return ReadOptionalInt32(retry, "count", 0);
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
