using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public abstract class DefaultConfigurationCreatorServiceBase: IDefaultConfigurationCreatorService
{
    private readonly ILogger<DefaultConfigurationCreatorServiceBase> _logger;
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    public DefaultConfigurationCreatorServiceBase(ILogger<DefaultConfigurationCreatorServiceBase> logger)
    {
        _logger = logger;
    }
    
    public async Task SetupAsync(string tenantId)
    {
        _logger.LogInformation("Setup tenant: '{TenantId}'", tenantId);

        if (!TenantsInHandling.TryAdd(tenantId, true))
        {
            _logger.LogWarning("Setup tenant already in work: '{TenantId}'", tenantId);
            return;
        }

        try
        {
            await SetupTenantAsync(tenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(tenantId, out _);
            _logger.LogInformation("Setup tenant handling done: '{TenantId}'", tenantId);
        }
    }

    protected abstract Task SetupTenantAsync(string tenantId);
}