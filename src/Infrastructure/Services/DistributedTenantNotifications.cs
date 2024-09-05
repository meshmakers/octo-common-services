using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

internal class DistributedTenantNotifications(IDistributionEventHubService distributionEventHubService)
    : ITenantNotifications
{
    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreCreateTenant(tenantId, correlationId));
    }

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosCreateTenant(tenantId, correlationId));
    }

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreUpdateTenant(tenantId, correlationId));
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosUpdateTenant(tenantId, correlationId));
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PreDeleteTenant(tenantId, correlationId));
    }

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        return distributionEventHubService.PublishAsync(new PosDeleteTenant(tenantId, correlationId));
    }
}