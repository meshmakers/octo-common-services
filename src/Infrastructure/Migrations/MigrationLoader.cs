using System.Reflection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public interface IMigrationLoader
{
    /// <summary>
    /// Returns all migration types that have the migration attribute.
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    IEnumerable<MigrationTypeContext> GetTypeContexts(Assembly assembly);

    IEnumerable<ConfigMigrationContext> GetMigrationContextsPerConfig(IEnumerable<IMigration> migrations);
}

public class MigrationLoader : IMigrationLoader
{
    /// <summary>
    /// Returns all migration types that have the migration attribute.
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public IEnumerable<MigrationTypeContext> GetTypeContexts(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(x => x.GetCustomAttribute<MigrationAttribute>() != null && 
                        typeof(IMigration).IsAssignableFrom(x) && !x.IsAbstract)
            .Select(x => new MigrationTypeContext(x, x.GetCustomAttribute<MigrationAttribute>()!));
    }

    public IEnumerable<ConfigMigrationContext> GetMigrationContextsPerConfig(IEnumerable<IMigration> migrations)
    {
        var migrationGroups = migrations
            .Select(x => new MigrationContext(x, x.GetType().GetCustomAttribute<MigrationAttribute>()!))
            .GroupBy(x => x.Attribute.ConfigName)
            .ToList();


        return migrationGroups.Select(g =>
        {
            var configName = g.Key;
            var migrationContexts = g.Select(x => new MigrationContext(x.Migration, x.Attribute))
                .OrderBy(x => x.Attribute.FromVersion).ToArray();
            return new ConfigMigrationContext(configName, migrationContexts);
        });
    }
}

/// <summary>
/// Type information for migrations;
/// </summary>
/// <param name="MigrationType"></param>
/// <param name="MigrationAttribute"></param>
public record MigrationTypeContext(Type MigrationType, MigrationAttribute MigrationAttribute);

/// <summary>
/// Encapsulates a single migration.
/// </summary>
/// <param name="Attribute"></param>
/// <param name="Migration"></param>
public record MigrationContext(IMigration Migration, MigrationAttribute Attribute);

/// <summary>
/// Encapsulates the ordered list of migrations for a specific configuration
/// </summary>
/// <param name="ConfigName"></param>
/// <param name="Migrations"></param>
public record ConfigMigrationContext(string ConfigName, MigrationContext[] Migrations);