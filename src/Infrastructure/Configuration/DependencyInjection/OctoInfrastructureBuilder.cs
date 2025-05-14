using System.Reflection;
using Meshmakers.Octo.Services.Infrastructure.Migrations;

namespace Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;

public interface IOctoInfrastructureBuilder
{
    public IServiceCollection Services { get; }
    
    public IOctoInfrastructureBuilder AddMigrations(Assembly assembly);
}

public class OctoInfrastructureBuilder : IOctoInfrastructureBuilder
{
    internal OctoInfrastructureBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }
    public IOctoInfrastructureBuilder AddMigrations(Assembly assembly)
    {
        var loader = new MigrationLoader();

        MigrationChecker.VerifyConsistency(loader, assembly);
        
        var typeContexts = loader.GetTypeContexts(assembly);


        // Register each migration type
        foreach (var context in typeContexts)
        {
            Services.AddTransient(typeof(IMigration), context.MigrationType);
        }

        Services.AddTransient<MigrationService>();
        Services.AddTransient<IMigrationLoader, MigrationLoader>();
        Services.AddTransient<IDbConfigVersionManager, DbConfigVersionManager>();

        return this;
    }
}