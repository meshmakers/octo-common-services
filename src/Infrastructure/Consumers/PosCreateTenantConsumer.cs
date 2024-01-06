using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

internal class PosCreateTenantConsumer: IDistributedConsumer<PosCreateTenant>
{
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;

    public PosCreateTenantConsumer(IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        return _defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId);
    }
}