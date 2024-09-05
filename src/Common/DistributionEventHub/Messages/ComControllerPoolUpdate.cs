using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller adapter update
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
/// <param name="Pools">Pools in cache</param>
/// <param name="Adapters">Adapters in cache</param>
public record ComControllerPoolUpdate(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    IEnumerable<PoolDescription> Pools,
    IEnumerable<PoolAdapterDescription> Adapters) : EventBase(CorrelationId, Timestamp);