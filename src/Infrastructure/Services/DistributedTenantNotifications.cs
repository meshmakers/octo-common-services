using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

internal class DistributedTenantNotifications(IDistributionEventHubService distributionEventHubService)
    : ITenantNotifications
{
    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreCreateTenant(tenantId, correlationId, DateTime.Now));
    }

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosCreateTenant(tenantId, correlationId, DateTime.Now));
    }

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId, correlationId, DateTime.Now));
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId, correlationId, DateTime.Now));
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreDeleteTenant(tenantId, correlationId, DateTime.Now));
    }

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosDeleteTenant(tenantId, correlationId, DateTime.Now));
    }
}