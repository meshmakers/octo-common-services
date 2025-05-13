using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public interface IDbConfigVersionManager
{
    Task<int> GetCurrentVersionAsync(string configName, IOctoAdminSession adminSession, ITenantContext tenantContext);
    Task UpdateVersionAsync(string configName, int version, IOctoAdminSession adminSession, ITenantContext tenantContext);
}