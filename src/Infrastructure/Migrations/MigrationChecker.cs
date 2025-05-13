using System.Reflection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public static class MigrationChecker
{
    public static void VerifyConsistency(IMigrationLoader loader, Assembly assembly)
    {
        var typeContexts = loader.GetTypeContexts(assembly).ToList();

        if (typeContexts.Count == 0)
        {
            throw MigrationException.NoMigrationsFound(assembly);
        }

        foreach (var contextByConfig in typeContexts.GroupBy(x => x.MigrationAttribute.ConfigName))
        {
            if (contextByConfig.All(x => x.MigrationAttribute.FromVersion != 0))
            {
                throw MigrationException.NoInitialMigrationFound(contextByConfig.Key);
            }

            var migrations = contextByConfig.OrderBy(x => x.MigrationAttribute.FromVersion).ToList();

            var exceptions = new List<MigrationException>();

            if (migrations.Any(x => x.MigrationAttribute.ToVersion == x.MigrationAttribute.FromVersion))
            {
                exceptions.Add(migrations.Where(x => x.MigrationAttribute.FromVersion == x.MigrationAttribute.ToVersion)
                    .Select(x =>
                        MigrationException.FromAndToVersionCantBeEqual(contextByConfig.Key,
                            x.MigrationAttribute.FromVersion, x.MigrationAttribute.ToVersion)).First());
            }

            if (migrations.Any(x => x.MigrationAttribute.ToVersion < x.MigrationAttribute.FromVersion))
            {
                exceptions.Add(migrations.Where(x => x.MigrationAttribute.ToVersion < x.MigrationAttribute.FromVersion)
                    .Select(x =>
                        MigrationException.ToVersionLowerThanFromVersion(contextByConfig.Key,
                            x.MigrationAttribute.FromVersion, x.MigrationAttribute.ToVersion)).First());
            }
            
            if(migrations.Any(x => x.MigrationAttribute.ToVersion < 0 || x.MigrationAttribute.FromVersion < 0))
            {
                exceptions.Add(migrations.Where(x => x.MigrationAttribute.ToVersion < 0 || x.MigrationAttribute.FromVersion < 0)
                    .Select(x =>
                        MigrationException.NegativeVersion(contextByConfig.Key,
                            x.MigrationAttribute.FromVersion, x.MigrationAttribute.ToVersion)).First());
            }
            
            

            for (var i = 1; i < migrations.Count; i++)
            {
                var current = migrations[i];
                var previous = migrations[i - 1];
                if (current.MigrationAttribute.FromVersion != previous.MigrationAttribute.ToVersion)
                {
                    exceptions.Add(MigrationException.GapInMigrationFound(contextByConfig.Key,
                        previous.MigrationAttribute.ToVersion, previous.MigrationAttribute.FromVersion));
                }
            }

            if (exceptions.Count > 0)
            {
                var message = string.Join(Environment.NewLine, exceptions.Select(x => x.Message));
                throw MigrationException.MultipleMigrationsHaveErrors(message);
            }
        }
    }
}