using System.Text;
using System.Text.Json;

namespace DeploymentProfiles;

public static class ProfileMigrator
{
    public static string ToVersion2(string json)
    {
        var profile = ProfileCodec.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
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

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static BatchMigrationResult MigrateBatch(
        IEnumerable<NamedProfile> entries, MigrationMode mode = MigrationMode.Atomic)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not (MigrationMode.Atomic or MigrationMode.BestEffort))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var profiles = new List<MigratedProfile>();
        var issues = new List<MigrationIssue>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var entry in entries)
        {
            var name = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                issues.Add(new MigrationIssue(index, name, "invalid-name"));
            else if (names.Contains(name))
                issues.Add(new MigrationIssue(index, name, "duplicate-name"));
            else
            {
                try
                {
                    var json = ToVersion2(entry!.Json);
                    names.Add(name);
                    profiles.Add(new MigratedProfile(name, json));
                }
                catch (FormatException)
                {
                    issues.Add(new MigrationIssue(index, name, "invalid-profile"));
                }
            }

            index++;
        }

        return new BatchMigrationResult(
            mode == MigrationMode.Atomic && issues.Count > 0 ? Array.Empty<MigratedProfile>() : profiles,
            issues);
    }
}
