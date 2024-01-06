using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging;
using PreUpdateTenant = Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.PreUpdateTenant;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

internal class CacheTenantConsumer 
    : IDistributedConsumer<PreUpdateTenant>,
        IDistributedConsumer<PreDeleteTenant>
{
    private readonly ICkCacheService _ckCacheService;
    private readonly ILogger<CacheTenantConsumer> _logger;

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
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        var key = context.Message.TenantId.NormalizeString();

        if (_ckCacheService.IsTenantLoaded(key))
        {
            _ckCacheService.Unload(key);
        }

        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _logger.LogInformation("Pre delete tenant received: {Text}", context.Message.TenantId);

        var key = context.Message.TenantId.NormalizeString();

        if (_ckCacheService.IsTenantLoaded(key))
        {
            _ckCacheService.Unload(key);
        }

        return Task.CompletedTask;
    }
}