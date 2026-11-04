namespace DeploymentProfiles;

/// <summary>Controls whether a batch returns successful profiles when row issues occur.</summary>
public enum MigrationMode
{
    /// <summary>Returns an empty profile list if any row produces an issue.</summary>
    Atomic,

    /// <summary>Returns all successfully migrated profiles alongside row issues.</summary>
    BestEffort
}

/// <summary>Associates a name with a profile document for batch migration.</summary>
/// <param name="Name">The name to trim and validate during migration.</param>
/// <param name="Json">The profile JSON to validate during migration.</param>
public sealed record NamedProfile(string Name, string Json);

/// <summary>Contains a successfully migrated profile and its trimmed name.</summary>
/// <param name="Name">The trimmed spelling supplied by the successful input row.</param>
/// <param name="Json">The compact version 2 profile JSON.</param>
public sealed record MigratedProfile(string Name, string Json);

/// <summary>Identifies the single migration issue reported for an input row.</summary>
/// <param name="Index">The zero-based input row index.</param>
/// <param name="Name">The trimmed input name, or null for a null row or name.</param>
/// <param name="Code">The issue code, invalid-name, duplicate-name, or invalid-profile.</param>
public sealed record MigrationIssue(int Index, string? Name, string Code);

/// <summary>Contains a batch's migrated profiles and row issues in their respective input orders.</summary>
/// <param name="Profiles">Successful profiles, empty when an atomic batch has any issue.</param>
/// <param name="Issues">Issues with at most one entry per input row.</param>
public sealed record BatchMigrationResult(
    IReadOnlyList<MigratedProfile> Profiles, IReadOnlyList<MigrationIssue> Issues);
