using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

internal class PosCreateTenantConsumer: IDistributedConsumer<PosCreateTenant>
{
    private readonly ILogger<PosCreateTenantConsumer> _logger;
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    public PosCreateTenantConsumer(ILogger<PosCreateTenantConsumer> logger, IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _logger = logger;
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        _logger.LogInformation("Pos create tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            _logger.LogWarning("Pos create tenant already in work: '{TenantId}'", context.Message.TenantId);
            return;
        }

        try
        {
            await _defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            _logger.LogInformation("Pos create tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }
}