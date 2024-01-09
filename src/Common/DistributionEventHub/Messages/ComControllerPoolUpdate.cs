using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller plug update
/// </summary>
public record ComControllerPoolUpdate(string TenantId, IEnumerable<PoolDescription> Pools, IEnumerable<PoolPlugDescription> Plugs);
