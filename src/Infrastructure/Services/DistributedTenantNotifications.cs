using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

internal class DistributedTenantNotifications : ITenantNotifications
{
    private readonly IDistributionEventHubService _distributionEventHubService;

    public DistributedTenantNotifications(IDistributionEventHubService distributionEventHubService)
    {
        _distributionEventHubService = distributionEventHubService;
    }
    
    public Task NotifyPreTenantCreateAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PreCreateTenant(tenantId));
    }

    public Task NotifyPosTenantCreateAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PosCreateTenant(tenantId));
    }

    public Task NotifyPreTenantUpdateAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId));
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId));
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PreDeleteTenant(tenantId));
    }

    public Task NotifyPosTenantDeleteAsync(string tenantId)
    {
        return _distributionEventHubService.PublishAsync(new PosDeleteTenant(tenantId));
    }
}