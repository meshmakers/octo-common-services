using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class PosCreatePosUpdateTenantConsumer(
    ILogger<PosCreatePosUpdateTenantConsumer> logger,
    ICkCacheService ckCacheService,
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

            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            logger.LogInformation("Pos update tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }
}