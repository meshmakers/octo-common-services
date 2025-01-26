using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging;
using PreUpdateTenant = Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.PreUpdateTenant;
// ReSharper disable InconsistentlySynchronizedField

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class CacheTenantConsumer 
    : IDistributedConsumer<PreUpdateTenant>,
        IDistributedConsumer<PreDeleteTenant>
{
    private readonly ICkCacheService _ckCacheService;
    private readonly ILogger<CacheTenantConsumer> _logger;
    private static readonly Lock Lock = new();

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="ckCacheService"></param>
    public CacheTenantConsumer(ILogger<CacheTenantConsumer> logger, ICkCacheService ckCacheService)
    {
        _logger = logger;
        _ckCacheService = ckCacheService;
    }

    public Task ConsumeAsync(IDistributedContext<PreUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: '{TenantId}'", context.Message.TenantId);

        var key = context.Message.TenantId.NormalizeString();

        lock (Lock)
        {
            if (_ckCacheService.IsTenantLoaded(key))
            {
                _logger.LogInformation("Pre update tenant unloading cache: '{TenantId}'", context.Message.TenantId);
                _ckCacheService.Unload(key);
            }
        }
        _logger.LogInformation("Pre update tenant handling done: '{TenantId}'", context.Message.TenantId);
        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _logger.LogInformation("Pre delete tenant received: {TenantId}", context.Message.TenantId);

        var key = context.Message.TenantId.NormalizeString();

        lock (Lock)
        {
            if (_ckCacheService.IsTenantLoaded(key))
            {
                _logger.LogInformation("Pre delete tenant unloading cache: '{TenantId}'", context.Message.TenantId);
                _ckCacheService.Unload(key);
            }
        }

        _logger.LogInformation("Pre delete tenant handling done: '{TenantId}'", context.Message.TenantId);
        return Task.CompletedTask;
    }
}