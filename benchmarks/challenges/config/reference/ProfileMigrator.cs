using System.Text.Json;

namespace DeploymentProfiles;

#if REV2
public enum MigrationMode { Atomic, BestEffort }
public sealed record NamedProfile(string Name, string Json);
public sealed record MigratedProfile(string Name, string Json);
public sealed record MigrationIssue(int Index, string? Name, string Code);
public sealed record BatchMigrationResult(IReadOnlyList<MigratedProfile> Profiles, IReadOnlyList<MigrationIssue> Issues);
#endif

public static class ProfileMigrator
{
    public static string ToVersion2(string json)
    {
        var profile = ProfileCodec.Parse(json);
        return JsonSerializer.Serialize(new
        {
            version = 2,
            transport = new { endpoint = profile.Endpoint, timeoutMs = profile.TimeoutMs },
            retry = new
            {
                count = profile.Retries,
#if REV2
                backoffMs = profile.BackoffMs
#endif
            },
            enabled = profile.Enabled
        });
    }

#if REV2
    public static BatchMigrationResult MigrateBatch(IEnumerable<NamedProfile> entries, MigrationMode mode = MigrationMode.Atomic)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not (MigrationMode.Atomic or MigrationMode.BestEffort))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<MigratedProfile>();
        var issues = new List<MigrationIssue>();
        var index = 0;
        foreach (var row in entries)
        {
            var name = row?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                issues.Add(new MigrationIssue(index, name, "invalid-name"));
            else if (names.Contains(name))
                issues.Add(new MigrationIssue(index, name, "duplicate-name"));
            else
            {
                try
                {
                    var migrated = ToVersion2(row!.Json);
                    names.Add(name);
                    profiles.Add(new MigratedProfile(name, migrated));
                }
                catch (FormatException)
                {
                    issues.Add(new MigrationIssue(index, name, "invalid-profile"));
                }
            }

            index++;
        }

        return new BatchMigrationResult(mode == MigrationMode.Atomic && issues.Count > 0 ? [] : profiles, issues);
    }
#endif
}
