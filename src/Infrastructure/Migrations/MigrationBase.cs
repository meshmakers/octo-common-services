using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Services.Infrastructure.Migrations;

public abstract class MigrationBase : IMigration
{
    public abstract Task<MigrationResult> MigrateAsync(IOctoAdminSession adminSession, ITenantContext tenantContext);
    /// <summary>
    /// Creates or updates an entity based on its well-known name
    /// </summary>
    /// <param name="session">Admin session</param>
    /// <param name="tenantContext">Tenant context</param>
    /// <param name="rtEntity">Entity to create or update</param>
    /// <typeparam name="TEntity">Type of the entity</typeparam>
    protected async Task CreateOrUpdateAsync<TEntity>(IOctoAdminSession session, ITenantContext tenantContext,
        TEntity rtEntity) where TEntity : RtEntity, new()
    {
        var tenantRepository = tenantContext.GetTenantRepositoryAsAdmin();

        DataQueryOperation operation = DataQueryOperation.Create();
        operation.FieldEquals(nameof(RtEntity.RtWellKnownName), rtEntity.RtWellKnownName);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<TEntity>(session, operation).ConfigureAwait(false);
        if (!result.Items.Any())
        {
            await tenantRepository.InsertOneRtEntityAsync(session, rtEntity).ConfigureAwait(false);
        }
        else
        {
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, result.Items.First().RtId, rtEntity)
                .ConfigureAwait(false);
        }
    }
}