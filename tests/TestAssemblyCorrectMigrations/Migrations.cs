using Meshmakers.Octo.Services.Infrastructure.Migrations;

namespace TestAssemblyCorrectMigrations;



[Migration(0,1, "Config1")]
public class Config1Migration1 : IMigration
{
    public Task<MigrationResult> MigrateAsync()
    {
        throw new NotImplementedException();
    }
}

[Migration(1, 2, "Config1")]
public class Config1Migration2 : IMigration
{
    public Task<MigrationResult> MigrateAsync()
    {
        throw new NotImplementedException();
    }
}


[Migration(0,1, "Config2")]
public class Config2Migration1 : IMigration
{
    public Task<MigrationResult> MigrateAsync()
    {
        throw new NotImplementedException();
    }
}

[Migration(1, 2, "Config2")]
public class Config2Migration2 : IMigration
{
    public Task<MigrationResult> MigrateAsync()
    {
        throw new NotImplementedException();
    }
}