using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

/// <summary>Converts individual profiles and named batches to a consistent version 2 JSON representation.</summary>
public static class ProfileMigrator
{
    /// <summary>Validates either profile format and writes compact version 2 JSON with explicit defaults.</summary>
    /// <param name="json">A version 1 or version 2 profile, with version 1 assumed when omitted.</param>
    /// <returns>
    /// JSON ordered as version, transport (endpoint, timeoutMs), retry (count, backoffMs), and enabled.
    /// Unknown properties are omitted, and migrating the result produces the same string.
    /// </returns>
    /// <exception cref="FormatException">The input is null or is not a valid supported profile.</exception>
    public static string ToVersion2(string json)
    {
        // Parsing first shares all validation and default handling with the deployment adapter.
        var profile = ProfileCodec.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Explicit writes keep property order stable and include every value, even defaults.
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

        // Disposing the writer flushes its UTF-8 bytes before they are converted to the result string.
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Migrates named profiles in input order, reserving trimmed names only after success.</summary>
    /// <param name="entries">The rows to enumerate once. Null rows and invalid names produce issues.</param>
    /// <param name="mode">Whether any issue discards all successes or permits partial results.</param>
    /// <returns>
    /// Successful profiles and at most one issue per row, each in input order.
    /// Names are trimmed and compared ordinally without case. Reserved names precede JSON validation.
    /// Atomic batches return no profiles when any issue occurs.
    /// </returns>
    /// <exception cref="ArgumentNullException">The entries collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode is unknown.</exception>
    public static BatchMigrationResult MigrateBatch(
        IEnumerable<NamedProfile> entries, MigrationMode mode = MigrationMode.Atomic)
    {
        // Reject invalid arguments before touching a potentially stateful input sequence.
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not (MigrationMode.Atomic or MigrationMode.BestEffort))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown migration mode.");

        var profiles = new List<MigratedProfile>();
        var issues = new List<MigrationIssue>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputIndex = 0;

        // One pass keeps indexes tied to source rows and preserves both result orders.
        foreach (var entry in entries)
        {
            var rowIndex = inputIndex++;
            var trimmedName = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(trimmedName))
            {
                issues.Add(new MigrationIssue(rowIndex, trimmedName, "invalid-name"));
                continue;
            }

            // A successful earlier row owns its name even when this row's JSON is invalid.
            if (reservedNames.Contains(trimmedName))
            {
                issues.Add(new MigrationIssue(rowIndex, trimmedName, "duplicate-name"));
                continue;
            }

            // Reuse single-profile migration and reserve the name only after validation succeeds.
            try
            {
                var migratedJson = ToVersion2(entry!.Json);
                profiles.Add(new MigratedProfile(trimmedName, migratedJson));
                reservedNames.Add(trimmedName);
            }
            catch (FormatException)
            {
                issues.Add(new MigrationIssue(rowIndex, trimmedName, "invalid-profile"));
            }
        }

        // Finish the pass before applying atomic output suppression so every row can report an issue.
        IReadOnlyList<MigratedProfile> returnedProfiles = mode == MigrationMode.Atomic && issues.Count > 0
            ? Array.Empty<MigratedProfile>()
            : profiles;
        return new BatchMigrationResult(returnedProfiles, issues);
    }
}
