namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     The Identity service wrote the service-owned identity data of a tenant, but the tenant itself has no roles
///     yet (its identity default configuration is not seeded). Expected and transient for a young tenant (AB#8117);
///     the tenant stays <c>Creating</c> and is retried.
/// </summary>
public sealed class IdentityDataSeedPendingException(string tenantId)
    : InvalidOperationException(
        $"Identity default configuration for tenant '{tenantId}' is not seeded yet " +
        "(the tenant has no roles); retry pending");
