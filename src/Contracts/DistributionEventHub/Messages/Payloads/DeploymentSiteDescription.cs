using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Represents a deployment site
/// </summary>
public record DeploymentSiteDescription
{
    /// <summary>
    /// Returns the name of the deployment site
    /// </summary>
    public string DeploymentSiteName { get; set; } = null!;
    
    /// <summary>
    /// Returns the runtime id of the deployment site
    /// </summary>
    public OctoObjectId DeploymentSiteRtId { get; set; }

    /// <summary>
    /// Returns the connection id of the deployment site
    /// </summary>
    public string? ConnectionId { get; set; }
}