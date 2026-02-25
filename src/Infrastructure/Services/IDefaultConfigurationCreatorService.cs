namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Interface used to creating basic configuration for a tenant
/// </summary>
public interface IDefaultConfigurationCreatorService
{
    /// <summary>
    ///     Initializes the default configuration
    /// </summary>
    /// <returns></returns>
    Task InitializeAsync();

    /// <summary>
    ///     Setups the default configuration for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant id, if null the system tenant is used.</param>
    /// <returns></returns>
    Task SetupAsync(string tenantId);

    /// <summary>
    ///     When true, <see cref="SetupAsync"/> will defer the tenant start phase
    ///     (the call to StartTenantAsync) until <see cref="StartDeferredTenantsAsync"/> is called.
    ///     This allows the distribution event hub to be started between tenant setup and tenant start,
    ///     so that services which send bus commands during StartTenantAsync don't time out.
    /// </summary>
    bool DeferTenantStart { get; set; }

    /// <summary>
    ///     Starts all tenants that were deferred during <see cref="SetupAsync"/> when
    ///     <see cref="DeferTenantStart"/> was true. Resets <see cref="DeferTenantStart"/> to false.
    /// </summary>
    Task StartDeferredTenantsAsync();

    /// <summary>
    ///     Retries startup for tenants that failed during <see cref="StartDeferredTenantsAsync"/>.
    ///     Tenants that succeed are removed from the failed tenant registry.
    ///     Tenants that exceed the maximum retry count are removed and logged as permanently failed.
    /// </summary>
    Task RetryFailedTenantsAsync();
}