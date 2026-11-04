using System.Text.Json;

namespace DeploymentProfiles;

public enum MigrationMode { Atomic, BestEffort }

public sealed record NamedProfile(string Name, string Json);

public sealed record MigratedProfile(string Name, string Json);

public sealed record MigrationIssue(int Index, string? Name, string Code);

public sealed record BatchMigrationResult(IReadOnlyList<MigratedProfile> Profiles, IReadOnlyList<MigrationIssue> Issues);

public static class ProfileMigrator
{
    public static string ToVersion2(string json)
    {
        var profile = ProfileCodec.Parse(json);
        return JsonSerializer.Serialize(new
        {
            version = 2,
            transport = new { endpoint = profile.Endpoint, timeoutMs = profile.TimeoutMs },
            retry = new { count = profile.Retries, backoffMs = profile.BackoffMs },
            enabled = profile.Enabled
        });
    }

    public static BatchMigrationResult MigrateBatch(IEnumerable<NamedProfile> entries, MigrationMode mode = MigrationMode.Atomic)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (mode is not (MigrationMode.Atomic or MigrationMode.BestEffort))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var profiles = new List<MigratedProfile>();
        var issues = new List<MigrationIssue>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var entry in entries)
        {
            var name = entry?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                issues.Add(new MigrationIssue(index, name, "invalid-name"));
            }
            else if (reservedNames.Contains(name))
            {
                issues.Add(new MigrationIssue(index, name, "duplicate-name"));
            }
            else
            {
                try
                {
                    var json = ToVersion2(entry!.Json);
                    profiles.Add(new MigratedProfile(name, json));
                    reservedNames.Add(name);
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
