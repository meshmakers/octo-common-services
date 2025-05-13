namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

/// <summary>
/// Interface for migration operations.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Run the migration process.
    /// </summary>
    /// <returns></returns>
    Task<MigrationResult> MigrateAsync();
}

public class MigrationResult
{
    public bool HasError { get; private init; }
    public string? ErrorText { get; private init; }

    public static MigrationResult Success() => new() { HasError = false };
    public static MigrationResult Failure(string error) => new() { HasError = true, ErrorText = error };
}