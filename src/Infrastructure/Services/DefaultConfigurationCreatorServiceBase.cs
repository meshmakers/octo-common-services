using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public abstract class DefaultConfigurationCreatorServiceBase(ILogger<DefaultConfigurationCreatorServiceBase> logger)
    : IDefaultConfigurationCreatorService
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task SetupAsync(string tenantId)
    {
        logger.LogInformation("Setup tenant: '{TenantId}'", tenantId);

        if (!TenantsInHandling.TryAdd(tenantId, true))
        {
            logger.LogWarning("Setup tenant already in work: '{TenantId}'", tenantId);
            return;
        }

        try
        {
            await SetupTenantAsync(tenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(tenantId, out _);
            logger.LogInformation("Setup tenant handling done: '{TenantId}'", tenantId);
        }
    }

    protected abstract Task SetupTenantAsync(string tenantId);
}