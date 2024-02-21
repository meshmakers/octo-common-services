using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller adapter update
/// </summary>
public record ComControllerPoolUpdate(string TenantId, IEnumerable<PoolDescription> Pools, IEnumerable<PoolAdapterDescription> Adapters);
