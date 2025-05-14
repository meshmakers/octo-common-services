using System.Reflection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public class MigrationException : Exception
{
    private MigrationException(string message) : base(message)
    {
    }

    public static MigrationException NoMigrationsFound(Assembly assembly) =>
        new($"No migrations found in assembly '{assembly.FullName}'");

    public static MigrationException NoInitialMigrationFound(string configName) =>
        new($"No initial migration (FromVersion=0) found for config {configName}.");

    public static MigrationException GapInMigrationFound(string configName, int previousToVersion,
        int currentFromVersion)
        => new("Gap in migration found. Previous migration ToVersion: " + previousToVersion +
               ", current migration FromVersion: " + currentFromVersion + ". Config: " + configName);

    public static MigrationException FromAndToVersionCantBeEqual(string configName, int fromVersion, int toVersion)
        => new("From and To version can't be equal. Config: " + configName +
               ", FromVersion: " + fromVersion + ", ToVersion: " + toVersion);

    public static MigrationException ToVersionLowerThanFromVersion(string configName, int fromVersion, int toVersion)
        => new("To version can't be lower than From version. Config: " + configName +
               ", FromVersion: " + fromVersion + ", ToVersion: " + toVersion);

    public static MigrationException NegativeVersion(string configName, int fromVersion, int toVersion)
        => new("Version can't be negative. Config: " + configName +
               ", FromVersion: " + fromVersion + ", ToVersion: " + toVersion);

    public static MigrationException MultipleMigrationsHaveErrors(string message) => new(message);
}