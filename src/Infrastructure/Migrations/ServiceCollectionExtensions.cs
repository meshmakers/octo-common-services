using System.Reflection;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMigrations(this IServiceCollection services, Assembly assembly)
    {
        var loader = new MigrationLoader();

        MigrationChecker.VerifyConsistency(loader, assembly);
        
        var typeContexts = loader.GetTypeContexts(assembly);


        // Register each migration type
        foreach (var context in typeContexts)
        {
            services.AddTransient(typeof(IMigration), context.MigrationType);
        }

        services.AddSingleton<MigrationService>();
        services.AddSingleton<MigrationLoader>();

        return services;
    }
}