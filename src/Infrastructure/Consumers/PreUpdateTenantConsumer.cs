using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;
using PreUpdateTenant = Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.PreUpdateTenant;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

/// <summary>
///     Handles the <see cref="PreUpdateTenant" /> message.
/// </summary>
internal class PreUpdateTenantConsumer : IDistributedConsumer<PreUpdateTenant>
{
    private readonly ICkCacheService _ckCacheService;
    private readonly ILogger<PreUpdateTenantConsumer> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="ckCacheService"></param>
    public PreUpdateTenantConsumer(ILogger<PreUpdateTenantConsumer> logger, ICkCacheService ckCacheService)
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
}