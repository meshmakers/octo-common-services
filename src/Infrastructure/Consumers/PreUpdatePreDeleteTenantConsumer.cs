using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

// ReSharper disable InconsistentlySynchronizedField

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

/// <summary>
/// Consumer for pre-update and pre-delete tenant messages.
/// This consumer handles the pre-update and pre-delete events for tenants by unloading the cache
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class PreUpdatePreDeleteTenantConsumer(
    ILogger<PreUpdatePreDeleteTenantConsumer> logger,
    ICkCacheService ckCacheService)
    : IDistributedConsumer<PreUpdateTenant>, IDistributedConsumer<PreDeleteTenant>
{
    private static readonly Lock Lock = new();

    public Task ConsumeAsync(IDistributedContext<PreUpdateTenant> context)
    {
        logger.LogInformation("Pre update tenant received: '{TenantId}'", context.Message.TenantId);

        var tenantId = context.Message.TenantId.NormalizeString();

        lock (Lock)
        {
            try
            {
                if (ckCacheService.IsTenantLoaded(tenantId))
                {
                    logger.LogInformation("Pre update tenant unloading cache: '{TenantId}'", tenantId);
                    ckCacheService.Unload(tenantId);
                }
            }
            finally
            {
                logger.LogInformation("Pos update tenant handling done: '{TenantId}'", tenantId);
            }
        }

        logger.LogInformation("Pre update tenant handling done: '{TenantId}'", tenantId);
        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        logger.LogInformation("Pre delete tenant received: {TenantId}", context.Message.TenantId);

        var tenantId = context.Message.TenantId.NormalizeString();

        lock (Lock)
        {
            try
            {
                if (ckCacheService.IsTenantLoaded(tenantId))
                {
                    logger.LogInformation("Pre delete tenant unloading cache: '{TenantId}'", context.Message.TenantId);
                    ckCacheService.Unload(tenantId);
                }
            }
            finally
            {
                logger.LogInformation("Pos delete tenant handling done: '{TenantId}'", tenantId);
            }
        }

        logger.LogInformation("Pre delete tenant handling done: '{TenantId}'", context.Message.TenantId);
        return Task.CompletedTask;
    }
}