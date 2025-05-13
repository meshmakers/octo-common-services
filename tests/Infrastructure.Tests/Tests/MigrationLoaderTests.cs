using Meshmakers.Octo.Services.Infrastructure.Migrations;
using TestAssemblyCorrectMigrations;
using Xunit;

namespace Infrastructure.Tests.Tests;

public class MigrationLoaderTests
{
    
    [Fact]
    public void GetTypeContexts_ValidAssembly_ReturnsMigrations()
    {
        var loader = new MigrationLoader();

        var contexts = loader.GetTypeContexts(typeof(Config1Migration2).Assembly).ToList();
        
        Assert.NotEmpty(contexts);

        Assert.Equal(4, contexts.Count);

        
        Assert.Contains(contexts, context => context.MigrationAttribute.ConfigName == "Config1");
        Assert.Contains(contexts, context => context.MigrationAttribute.ConfigName == "Config2");
    }

    [Fact]
    public void GetMigrationContextsPerConfig_ValidMigrations_ReturnsGroupedMigrations()
    {
        var loader = new MigrationLoader();

        var contexts = loader.GetMigrationContextsPerConfig(GetCorrectMigrationsUnsorted()).ToList();

        Assert.NotEmpty(contexts);
        Assert.Equal(2, contexts.Count);

        Assert.Contains(contexts, context => context.ConfigName == "Config1");
        Assert.Contains(contexts, context => context.ConfigName == "Config2");

        Assert.Equal(2, contexts.First(x => x.ConfigName == "Config1").Migrations.Length);
        Assert.Equal(2, contexts.First(x => x.ConfigName == "Config2").Migrations.Length);
    }

    [Fact]
    public void GetMigrationContextsPerConfig_ValidMigrations_ReturnsMigrationsInCorrectOrder()
    {
        var loader = new MigrationLoader();
        
        var contexts = loader.GetMigrationContextsPerConfig(GetCorrectMigrationsUnsorted()).ToList();
        
        var config1Migrations = contexts.First(x => x.ConfigName == "Config1").Migrations;
        var config2Migrations = contexts.First(x => x.ConfigName == "Config2").Migrations;

        Assert.Equal(0, config1Migrations[0].Attribute.FromVersion);
        Assert.Equal(1, config1Migrations[0].Attribute.ToVersion);
        Assert.Equal(1, config1Migrations[1].Attribute.FromVersion);
        Assert.Equal(2, config1Migrations[1].Attribute.ToVersion);
        
        Assert.Equal(0, config2Migrations[0].Attribute.FromVersion);
        Assert.Equal(1, config2Migrations[0].Attribute.ToVersion);
        Assert.Equal(1, config2Migrations[1].Attribute.FromVersion);
        Assert.Equal(2, config2Migrations[1].Attribute.ToVersion);
    }

    private static IEnumerable<IMigration> GetCorrectMigrationsUnsorted()
    {
        return [new Config2Migration2(), new Config2Migration1(), new Config1Migration1(), new Config1Migration2()];
    }
}