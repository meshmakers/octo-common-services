using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public abstract class DefaultConfigurationCreatorServiceBase(ILogger<DefaultConfigurationCreatorServiceBase> logger)
    : IDefaultConfigurationCreatorService
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    /// <inheritdoc />
    public bool DeferTenantStart { get; set; }

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

            // Phase 2 of the platform-services initiative — lifecycle-only refresh hook.
            // `DeferTenantStart` is true during the cold-start initialization loop, false
            // during attach / restore / manual Enable. Skipping the refresh on cold-start
            // avoids the failure mode where a pod restart resets every tenant's runtime
            // state on a service that uses the hook for force-re-apply work (see the
            // admin-panel `System.TenantMode` runbook for why this gate matters). On
            // attach / restore / Enable the tenant has either just arrived (no install row
            // for the current cluster's helm vars) or just transitioned, so a force pass
            // is the safe default.
            if (!DeferTenantStart)
            {
                await RefreshTenantStateAsync(tenantId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup tenant failed: '{TenantId}'", tenantId);
            throw;
        }
        finally
        {
            TenantsInHandling.Remove(tenantId, out _);
            logger.LogInformation("Setup tenant handling done: '{TenantId}'", tenantId);
        }
    }

    /// <inheritdoc />
    public virtual Task StartDeferredTenantsAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task RetryFailedTenantsAsync()
    {
        return Task.CompletedTask;
    }

    protected abstract Task SetupTenantAsync(string tenantId);

    /// <summary>
    ///     Hook for tenant-online refresh logic that must NOT fire during the cold-start
    ///     initialization loop. Called from <see cref="SetupAsync"/> after
    ///     <see cref="SetupTenantAsync"/> completes, only when
    ///     <see cref="IDefaultConfigurationCreatorService.DeferTenantStart"/> is false —
    ///     i.e. on attach / restore / manual <see cref="IConfigurationService.EnableAsync"/>
    ///     (the Standardized base also invokes the hook at the tail of its
    ///     <c>EnableAsync</c> path; see <see cref="DefaultConfigurationCreatorServiceStandardized.EnableAsync"/>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Default no-op — services opt in by overriding with their tenant-online refresh
    ///         logic. Admin-Panel's <c>RefreshTenantModeAsync</c> is the proven example: it
    ///         force-re-applies the <c>System.TenantMode</c> blueprint so a tenant restored
    ///         from prod-1 onto test-2 lands the test-2 EnvironmentMode value instead of the
    ///         source cluster's Production. Identity's expected override (when added) is
    ///         the child-tenant identity-data write-through documented in
    ///         <c>octo-platform-services/docs/concepts/phase-2-ck-blueprint-seed-management.md</c>
    ///         §2 / §4.2.
    ///     </para>
    ///     <para>
    ///         Failures should generally be logged but not propagated, mirroring Admin-Panel's
    ///         pattern — at this point the tenant is already operational, a refresh miss is a
    ///         degradation rather than a hard failure. Throwing from the hook bubbles up
    ///         through <see cref="SetupAsync"/> and propagates to the lifecycle-event consumer.
    ///     </para>
    /// </remarks>
    protected virtual Task RefreshTenantStateAsync(string tenantId) => Task.CompletedTask;
}