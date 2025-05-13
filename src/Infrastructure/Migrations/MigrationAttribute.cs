namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class MigrationAttribute : Attribute
{
    public int FromVersion { get; }
    public int ToVersion { get; }
    public string ConfigName { get; }
    public string? Description { get; }

    public MigrationAttribute(int fromVersion, int toVersion, string configName, string? description = null)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        ConfigName = configName;
        Description = description;
    }
}