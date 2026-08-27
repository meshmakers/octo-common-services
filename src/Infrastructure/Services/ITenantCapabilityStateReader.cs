using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Reads which <see cref="TenantCapability" /> flags are enabled for a tenant, straight from the
///     tenant's own configuration store — no call to the owning services (AB#4255).
/// </summary>
/// <remarks>
///     A capability counts as enabled only when its flag exists <b>and</b> reads <c>IsEnabled = true</c>.
///     A missing key and a kept <c>false</c> flag are both "disabled", because Communication, Reporting
///     and AI delete their key on Disable while Stream Data keeps it with <c>false</c>. Read failures
///     propagate to the caller: an unreadable state is never reported as "disabled".
/// </remarks>
public interface ITenantCapabilityStateReader
{
    /// <summary>
    ///     Returns the enabled capabilities of the tenant behind <paramref name="tenantContext" />, in
    ///     <see cref="TenantCapability" /> declaration order.
    /// </summary>
    /// <param name="tenantContext">Context of the tenant whose flags are read</param>
    Task<IReadOnlyList<TenantCapability>> GetEnabledCapabilitiesAsync(ITenantContext tenantContext);

    /// <summary>
    ///     Resolves <paramref name="childTenantId" /> as a direct child of <paramref name="parentContext" />
    ///     and returns its enabled capabilities, in <see cref="TenantCapability" /> declaration order.
    /// </summary>
    /// <param name="parentContext">Context of the parent tenant</param>
    /// <param name="childTenantId">ID of the child tenant</param>
    /// <exception cref="TenantException">
    ///     The tenant is not a direct child of the parent (<c>IsTenantNotFound</c>).
    /// </exception>
    Task<IReadOnlyList<TenantCapability>> GetEnabledCapabilitiesAsync(ITenantContext parentContext,
        string childTenantId);
}
