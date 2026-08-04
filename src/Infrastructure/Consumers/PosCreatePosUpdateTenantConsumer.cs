using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class PosCreatePosUpdateTenantConsumer(
    ILogger<PosCreatePosUpdateTenantConsumer> logger,
    ICkCacheService ckCacheService,
    ISystemContext systemContext,
    IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    : IDistributedConsumer<PosCreateTenant>, IDistributedConsumer<PosUpdateTenant>
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    private void UnloadCacheIfLoaded(string tenantId)
    {
        if (ckCacheService.IsTenantLoaded(tenantId))
        {
            logger.LogInformation("Pos update/create tenant unloading cache: '{TenantId}'", tenantId);
            ckCacheService.Unload(tenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        logger.LogInformation("Pos create tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            logger.LogWarning("Pos update or create tenant already in work: '{TenantId}'", context.Message.TenantId);
            return;
        }

        try
        {
            var tenantId = context.Message.TenantId.NormalizeString();
            UnloadCacheIfLoaded(tenantId);

            // Belt and braces for AB#4690: the delete-side invalidation already drops the cached MongoDB
            // clients of a tenant's database, but a resolve in the short window between that event and the
            // physical drop could have re-populated the cache with connections authenticated as the
            // now-dropped user. Dropping them again here — before any setup work touches the database —
            // makes a re-created tenant independent of that timing. Best-effort: on PosCreateTenant the
            // tenant record may not be committed yet, in which case the database name cannot be resolved
            // and the (now durable) setup retry covers the next attempt.
            await systemContext.InvalidateTenantRepositoryClientsAsync(tenantId).ConfigureAwait(false);

            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            logger.LogInformation("Pos create tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        logger.LogInformation("Pos update tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            logger.LogWarning("Pos update or create tenant already in work: '{TenantId}'", context.Message.TenantId);
            return;
        }

        try
        {
            var tenantId = context.Message.TenantId.NormalizeString();
            UnloadCacheIfLoaded(tenantId);

            // Belt and braces for AB#4690: the delete-side invalidation already drops the cached MongoDB
            // clients of a tenant's database, but a resolve in the short window between that event and the
            // physical drop could have re-populated the cache with connections authenticated as the
            // now-dropped user. Dropping them again here — before any setup work touches the database —
            // makes a re-created tenant independent of that timing. Best-effort: on PosCreateTenant the
            // tenant record may not be committed yet, in which case the database name cannot be resolved
            // and the (now durable) setup retry covers the next attempt.
            await systemContext.InvalidateTenantRepositoryClientsAsync(tenantId).ConfigureAwait(false);

            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            logger.LogInformation("Pos update tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }
}