using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

internal class PosCreateTenantConsumer: IDistributedConsumer<PosCreateTenant>
{
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;
    private readonly ConcurrentDictionary<string, bool> _tenantsInHandling = new();

    public PosCreateTenantConsumer(IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        if (!_tenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            return;
        }

        try
        {
            await _defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId);
        }
        finally
        {
            _tenantsInHandling.Remove(context.Message.TenantId, out _);
        }
    }
}