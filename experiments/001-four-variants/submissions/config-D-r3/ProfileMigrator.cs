using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Converts individual profiles or named batches into a stable version 2 representation.</summary>
public static class ProfileMigrator
{
    /// <summary>Serializes a v1 or v2 profile as compact v2 JSON in a fixed property order.</summary>
    /// <param name="json">The profile JSON to validate and migrate.</param>
    /// <returns>
    /// JSON containing all recognized v2 settings, including defaults, without unknown fields.
    /// The retry object contains count followed by backoffMs, including zero values.
    /// Migrating this output again produces the same string.
    /// </returns>
    /// <exception cref="FormatException">The input is not a valid supported profile.</exception>
    public static string ToVersion2(string json)
    {
        // Reuse parsing so migration applies exactly the same validation and defaults
        // as deployment requests, including validation of disabled profiles.
        var profile = ProfileCodec.Parse(json);
        using var buffer = new MemoryStream();

        // Explicit member writes fix the property order. The writer handles endpoint
        // escaping and emits compact JSON with its default options.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);

            writer.WriteStartObject("transport");
            writer.WriteString("endpoint", profile.Endpoint);
            writer.WriteNumber("timeoutMs", profile.TimeoutMs);
            writer.WriteEndObject();

            writer.WriteStartObject("retry");
            writer.WriteNumber("count", profile.Retries);
            writer.WriteNumber("backoffMs", profile.BackoffMs);
            writer.WriteEndObject();
            writer.WriteBoolean("enabled", profile.Enabled);
            writer.WriteEndObject();
        }

        // Disposing the writer flushes its bytes before the buffer is decoded.
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Migrates named profiles in input order, reporting at most one issue per row.</summary>
    /// <param name="entries">The entries to enumerate once. Null rows produce invalid-name issues.</param>
    /// <param name="mode">Whether any issue suppresses all profiles or preserves successful rows.</param>
    /// <returns>
    /// Successful migrations and issues in their respective input order. Atomic mode returns no
    /// profiles if any issue occurs. Names are trimmed and issue indexes are zero-based.
    /// </returns>
    /// <remarks>
    /// The first successful migration reserves its name using ordinal case-insensitive comparison.
    /// Reserved names are rejected before JSON validation. Invalid profiles do not reserve names.
    /// All rows are processed in either mode. Exceptions from enumeration propagate to the caller.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The entries collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode is unsupported.</exception>
    public static BatchMigrationResult MigrateBatch(
        IEnumerable<NamedProfile> entries, MigrationMode mode = MigrationMode.Atomic)
    {
        // Check arguments before obtaining the caller's enumerator, including for empty batches.
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not (MigrationMode.Atomic or MigrationMode.BestEffort))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported migration mode.");

        var profiles = new List<MigratedProfile>();
        var issues = new List<MigrationIssue>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var entry in entries)
        {
            // Normalize once so validation, reservation, output, and issues share the same spelling.
            var name = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                issues.Add(new MigrationIssue(index, name, "invalid-name"));
            else if (reservedNames.Contains(name))
                issues.Add(new MigrationIssue(index, name, "duplicate-name"));
            else
            {
                // Single-profile migration owns JSON validation. Reserve names only after success
                // so an invalid profile cannot prevent a later valid row from using its name.
                try
                {
                    var migratedJson = ToVersion2(entry!.Json);
                    profiles.Add(new MigratedProfile(name, migratedJson));
                    reservedNames.Add(name);
                }
                catch (FormatException)
                {
                    issues.Add(new MigrationIssue(index, name, "invalid-profile"));
                }
            }

            index++;
        }

        // Apply atomic filtering after the single pass so every row still contributes its issues.
        return new BatchMigrationResult(
            mode == MigrationMode.Atomic && issues.Count > 0 ? Array.Empty<MigratedProfile>() : profiles,
            issues);
    }
}
