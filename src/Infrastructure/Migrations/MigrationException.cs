using System.Diagnostics;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public class MigrationException : Exception
{
    private MigrationException(string message) : base(message)
    {
    }

    public static MigrationException NoMigrationsFound() => new("No migrations found.");

    public static MigrationException NoInitialMigrationFound(string configName) =>
        new($"No initial migration (FromVersion=0) found for config {configName}.");

    public static MigrationException GapInMigrationFound(string configName, int previousToVersion, int currentFromVersion)
    => new("Gap in migration found. Previous migration ToVersion: " + previousToVersion +
           ", current migration FromVersion: " + currentFromVersion + ". Config: " + configName);
}