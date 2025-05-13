using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

/// <summary>
/// Interface for migration operations.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Run the migration process.
    /// </summary>
    /// <param name="adminSession">The admin session for the migration.</param>
    /// <param name="tenantContext">The tenant context for the migration.</param>
    /// <returns>The result of the migration operation.</returns>
    Task<MigrationResult> MigrateAsync(IOctoAdminSession adminSession, ITenantContext tenantContext);
}

public class MigrationResult
{
    public bool HasError { get; private init; }
    public string? ErrorText { get; private init; }

    public static MigrationResult Success() => new() { HasError = false };
    public static MigrationResult Failure(string error) => new() { HasError = true, ErrorText = error };
}