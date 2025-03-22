using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class PosCreateTenantConsumer(
    ILogger<PosCreateTenantConsumer> logger,
    IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    : IDistributedConsumer<PosCreateTenant>
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        logger.LogInformation("Pos create tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            logger.LogWarning("Pos create tenant already in work: '{TenantId}'", context.Message.TenantId);
            return;
        }

        try
        {
            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            logger.LogInformation("Pos create tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }
}