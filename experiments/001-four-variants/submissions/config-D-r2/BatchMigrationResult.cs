namespace DeploymentProfiles;

/// <summary>Controls whether batch issues suppress successfully migrated profiles.</summary>
public enum MigrationMode
{
    /// <summary>Returns no profiles when any row has an issue.</summary>
    Atomic,

    /// <summary>Returns all successful profiles alongside any issues.</summary>
    BestEffort
}

/// <summary>Pairs an input name with profile JSON for batch migration.</summary>
/// <param name="Name">The name to trim and validate during migration.</param>
/// <param name="Json">The v1 or v2 profile JSON to migrate.</param>
public sealed record NamedProfile(string Name, string Json);

/// <summary>Pairs a successfully migrated profile with its trimmed input name.</summary>
/// <param name="Name">The trimmed spelling of the successful input name.</param>
/// <param name="Json">The compact version 2 profile JSON.</param>
public sealed record MigratedProfile(string Name, string Json);

/// <summary>Identifies a row that failed name validation, duplicate detection, or profile migration.</summary>
/// <param name="Index">The zero-based position of the input row.</param>
/// <param name="Name">The trimmed input name, empty for whitespace or null for a null name or row.</param>
/// <param name="Code">The issue code, invalid-name, duplicate-name, or invalid-profile.</param>
public sealed record MigrationIssue(int Index, string? Name, string Code);

/// <summary>Holds batch migrations and issues in their respective input order.</summary>
/// <param name="Profiles">Successful rows, or an empty list when an atomic batch has any issue.</param>
/// <param name="Issues">The issues collected with at most one per input row.</param>
public sealed record BatchMigrationResult(
    IReadOnlyList<MigratedProfile> Profiles, IReadOnlyList<MigrationIssue> Issues);
