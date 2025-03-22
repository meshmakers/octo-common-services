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
    FailedTenantHasNoIdentityCk = 2
}