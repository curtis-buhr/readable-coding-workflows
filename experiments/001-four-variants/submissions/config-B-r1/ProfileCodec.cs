using System.Text.Json;

namespace DeploymentProfiles;

public sealed record ServiceProfile(string Endpoint, int TimeoutMs, int Retries, bool Enabled);

/// <summary>Reads version 1 and version 2 deployment profiles into a shared model.</summary>
public static class ProfileCodec
{
    /// <summary>Parses a profile, treating an omitted version as version 1.</summary>
    /// <param name="json">A JSON object in either supported profile format.</param>
    /// <returns>A validated profile with defaults applied and the endpoint text preserved.</returns>
    /// <exception cref="FormatException">
    /// The input is null, malformed, has an unsupported version, or contains invalid settings.
    /// </exception>
    public static ServiceProfile Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Profile must be a JSON object.");

            // Select the layout first so fields belonging to the other version remain unknown.
            var version = ReadOptionalInteger(root, "version", 1);
            var transport = version switch
            {
                1 => root,
                2 => ReadRequiredObject(root, "transport"),
                _ => throw new FormatException("Unsupported profile version.")
            };

            // Both layouts use the same value readers and bounds after selecting their containers.
            var endpoint = ReadEndpoint(transport);
            var timeoutMilliseconds = ReadOptionalInteger(transport, "timeoutMs", 5000);
            var retryCount = version == 1
                ? ReadOptionalInteger(root, "retries", 0)
                : ReadVersion2RetryCount(root);
            var enabled = ReadOptionalBoolean(root, "enabled", true);
            if (timeoutMilliseconds is < 1 or > 60000 || retryCount is < 0 or > 5)
                throw new FormatException("Invalid transport settings.");

            return new ServiceProfile(endpoint, timeoutMilliseconds, retryCount, enabled);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentNullException)
        {
            // String decoding can reject malformed Unicode after parsing; keep all input errors consistent.
            throw new FormatException("Invalid profile.", exception);
        }
    }

    /// <summary>Reads a required JSON object property, rejecting missing or non-object values.</summary>
    /// <exception cref="FormatException">The property is absent or is not an object.</exception>
    private static JsonElement ReadRequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"'{propertyName}' must be an object.");

        return value;
    }

    /// <summary>Validates the required HTTP or HTTPS endpoint and returns its original text.</summary>
    /// <exception cref="FormatException">The endpoint is absent, has the wrong type, or is invalid.</exception>
    private static string ReadEndpoint(JsonElement transport)
    {
        if (!transport.TryGetProperty("endpoint", out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException("Endpoint must be a string.");

        // Uri is used only for validation; returning its representation could change the supplied text.
        var endpoint = value.GetString()!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            throw new FormatException("Invalid endpoint.");

        return endpoint;
    }

    /// <summary>Reads an optional Int32 JSON number, using the default only when absent.</summary>
    /// <exception cref="FormatException">The property is present but is not an Int32 number.</exception>
    private static int ReadOptionalInteger(JsonElement parent, string propertyName, int defaultValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            return defaultValue;

        // Check the JSON kind before conversion so strings, nulls, and overflowing numbers fail uniformly.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer))
            throw new FormatException($"'{propertyName}' must be an Int32 number.");

        return integer;
    }

    /// <summary>Reads an optional JSON boolean, using the default only when absent.</summary>
    /// <exception cref="FormatException">The property is present but is not a boolean.</exception>
    private static bool ReadOptionalBoolean(JsonElement parent, string propertyName, bool defaultValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            return defaultValue;

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"'{propertyName}' must be a boolean.");

        return value.GetBoolean();
    }

    /// <summary>Reads the version 2 retry count, defaulting an absent retry object or count to zero.</summary>
    /// <exception cref="FormatException">The retry container or count has an invalid JSON type.</exception>
    private static int ReadVersion2RetryCount(JsonElement root)
    {
        if (!root.TryGetProperty("retry", out _))
            return 0;

        var retry = ReadRequiredObject(root, "retry");
        return ReadOptionalInteger(retry, "count", 0);
    }
}
