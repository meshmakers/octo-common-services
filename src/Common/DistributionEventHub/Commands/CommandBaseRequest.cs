namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
/// Base class for commands
/// </summary>
public abstract record CommandBaseRequest
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id if null the system tenant is used.</param>
    protected CommandBaseRequest(string? tenantId)
    {
        TenantId = tenantId;
    }
    
    /// <summary>
    /// Returns the tenant id
    /// </summary>
    public string? TenantId { get; }
}