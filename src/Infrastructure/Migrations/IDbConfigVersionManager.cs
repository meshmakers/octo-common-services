namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public interface IDbConfigVersionManager
{
    Task<int> GetCurrentVersionAsync(string configName);
    Task UpdateVersionAsync(string configName, int version);
}