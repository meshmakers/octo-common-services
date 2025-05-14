using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public interface IDbConfigVersionManager
{
    Task<int> GetCurrentVersionAsync(string configName, IOctoAdminSession adminSession, ITenantContext tenantContext);

    Task UpdateVersionAsync(string configName, int version, IOctoAdminSession adminSession,
        ITenantContext tenantContext);
}

public class DbConfigVersionManager : IDbConfigVersionManager
{
    public async Task<int> GetCurrentVersionAsync(string configName, IOctoAdminSession adminSession,
        ITenantContext tenantContext)
    {
        var info = await tenantContext
            .GetConfigurationAsync<DefaultConfigurationVersion>(adminSession, configName, new() { Version = 0 })
            .ConfigureAwait(false);
        return info?.Version ?? 0;
    }

    public Task UpdateVersionAsync(string configName, int version, IOctoAdminSession adminSession,
        ITenantContext tenantContext)
    {
        var info = new DefaultConfigurationVersion { Version = version };
        return tenantContext.SetConfigurationAsync(adminSession, configName, info);
    }
}