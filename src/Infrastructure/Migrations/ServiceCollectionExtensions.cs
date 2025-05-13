using System.Reflection;
using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public static class ServiceCollectionExtensions
{
    public static IOctoInfrastructureBuilder AddMigrations(this IOctoInfrastructureBuilder builder, Assembly assembly)
    {
        var loader = new MigrationLoader();

        MigrationChecker.VerifyConsistency(loader, assembly);
        
        var typeContexts = loader.GetTypeContexts(assembly);


        // Register each migration type
        foreach (var context in typeContexts)
        {
            builder.Services.AddTransient(typeof(IMigration), context.MigrationType);
        }

        builder.Services.AddTransient<MigrationService>();
        builder.Services.AddTransient<IMigrationLoader, MigrationLoader>();
        builder.Services.AddTransient<IDbConfigVersionManager, DbConfigVersionManager>();

        return builder;
    }
}