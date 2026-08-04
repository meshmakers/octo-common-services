namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
/// Represents the result of the creation of an identity data
/// </summary>
public enum CreateIdentityDataResult
{
    /// <summary>
    /// Undefined result
    /// </summary>
    Undefined = 0,
    
    /// <summary>
    /// The identity data was created successfully
    /// </summary>
    Success = 1,

    /// <summary>
    /// The identity data already exists
    /// </summary>
    FailedTenantHasNoIdentityCk = 2,

    /// <summary>
    /// The service-owned identity data (API scopes, resources, clients) was created, but the tenant's own
    /// identity default configuration is not seeded yet — it has no roles, so no administrator can be
    /// provisioned and no interactive login works.
    /// </summary>
    /// <remarks>
    /// Introduced by AB#4690. The consumer only ever created scopes / resources / clients and answered
    /// <see cref="Success"/> as soon as the tenant had the Identity CK model, which made the caller mark the
    /// tenant fully provisioned while the Identity service's own <c>SetupTenantAsync</c> (owner of the
    /// roles / groups seed) had in fact never run. A tenant could therefore sit at lifecycle state
    /// <c>Active</c> with zero roles indefinitely. Callers must treat this like a transient not-ready
    /// condition and retry, not as completion. Additive value: a producer that predates it simply never
    /// sends it, and a consumer that predates it falls into its "unexpected result" branch, which does not
    /// mark the tenant provisioned either.
    /// </remarks>
    SuccessIdentityDataSeedPending = 3
}