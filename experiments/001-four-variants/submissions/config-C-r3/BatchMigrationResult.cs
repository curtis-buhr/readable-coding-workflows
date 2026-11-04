namespace DeploymentProfiles;

public enum MigrationMode { Atomic, BestEffort }

public sealed record NamedProfile(string Name, string Json);

public sealed record MigratedProfile(string Name, string Json);

public sealed record MigrationIssue(int Index, string? Name, string Code);

public sealed record BatchMigrationResult(
    IReadOnlyList<MigratedProfile> Profiles,
    IReadOnlyList<MigrationIssue> Issues);
