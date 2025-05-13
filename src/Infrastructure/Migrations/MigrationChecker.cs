using System.Reflection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public static class MigrationChecker
{
    public static void VerifyConsistency(IMigrationLoader loader, Assembly assembly)
    {
        var typeContexts = loader.GetTypeContexts(assembly).ToList();

        if (typeContexts.Count == 0)
        {
            throw MigrationException.NoMigrationsFound();
        }

        foreach (var contextByConfig in typeContexts.GroupBy(x => x.MigrationAttribute.ConfigName))
        {
            if(contextByConfig.All(x => x.MigrationAttribute.FromVersion != 0))
            {
                throw MigrationException.NoInitialMigrationFound(contextByConfig.Key);
            }
            
            var migrations = contextByConfig.OrderBy(x=> x.MigrationAttribute.FromVersion).ToList();
            for (var i = 1; i < migrations.Count; i++)
            {
                var current = migrations[i];
                var previous = migrations[i - 1];
                if (current.MigrationAttribute.FromVersion != previous.MigrationAttribute.ToVersion)
                {
                    throw MigrationException.GapInMigrationFound(contextByConfig.Key, previous.MigrationAttribute.ToVersion, previous.MigrationAttribute.FromVersion);
                }
            }
            
        }
    }
}