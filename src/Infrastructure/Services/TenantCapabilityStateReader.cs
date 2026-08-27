using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Default implementation of <see cref="ITenantCapabilityStateReader" />. Stream data is read through
///     the engine's own <see cref="ITenantContext.IsStreamDataEnabledAsync" />; the other flags are the
///     <see cref="DefaultConfigurationEnabled" /> documents the owning services write under
///     <see cref="TenantCapabilityConfigurationKeys" />.
/// </summary>
public class TenantCapabilityStateReader : ITenantCapabilityStateReader
{
    private static readonly (TenantCapability Capability, string Key)[] ConfigurationBackedCapabilities =
    [
        (TenantCapability.Communication, TenantCapabilityConfigurationKeys.Communication),
        (TenantCapability.Reporting, TenantCapabilityConfigurationKeys.Reporting),
        (TenantCapability.AiServices, TenantCapabilityConfigurationKeys.AiServices),
    ];

    private readonly ILogger<TenantCapabilityStateReader> _logger;

    /// <summary>
    ///     Creates a new instance of <see cref="TenantCapabilityStateReader" />
    /// </summary>
    /// <param name="logger">Logger</param>
    public TenantCapabilityStateReader(ILogger<TenantCapabilityStateReader> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantCapability>> GetEnabledCapabilitiesAsync(ITenantContext parentContext,
        string childTenantId)
    {
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentValidation.ValidateString(nameof(childTenantId), childTenantId);

        ITenantContext? childContext;
        using (var session = await parentContext.GetAdminSessionAsync().ConfigureAwait(false))
        {
            session.StartTransaction();
            try
            {
                childContext = await parentContext.TryGetChildTenantContextAsync(session, childTenantId)
                    .ConfigureAwait(false);
                await session.CommitTransactionAsync().ConfigureAwait(false);
            }
            catch
            {
                await session.AbortTransactionAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (childContext == null)
        {
            throw TenantException.TenantDoesNotExist(childTenantId);
        }

        return await GetEnabledCapabilitiesAsync(childContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantCapability>> GetEnabledCapabilitiesAsync(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var enabled = new List<TenantCapability>();

        // Stream data has a first-class engine API; it treats a missing key as disabled as well.
        if (await tenantContext.IsStreamDataEnabledAsync().ConfigureAwait(false))
        {
            enabled.Add(TenantCapability.StreamData);
        }

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        try
        {
            foreach (var (capability, key) in ConfigurationBackedCapabilities)
            {
                // Communication, Reporting and AI delete the document on Disable, so the default
                // (false) is what a disabled tenant reads; a kept flag must still say true.
                var flag = await tenantContext.GetConfigurationAsync(session, key,
                    new DefaultConfigurationEnabled { IsEnabled = false }).ConfigureAwait(false);
                if (flag is { IsEnabled: true })
                {
                    enabled.Add(capability);
                }
            }

            await session.CommitTransactionAsync().ConfigureAwait(false);
        }
        catch
        {
            await session.AbortTransactionAsync().ConfigureAwait(false);
            throw;
        }

        enabled.Sort();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Tenant '{TenantId}' has {Count} enabled capabilities: {Capabilities}",
                tenantContext.TenantId, enabled.Count, string.Join(", ", enabled));
        }

        return enabled;
    }
}
