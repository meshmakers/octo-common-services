using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller adapter update
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
/// <param name="Adapters">Adapters in cache</param>
public record ComControllerAdapterUpdate(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    IEnumerable<AdapterDescription> Adapters) : EventBase(CorrelationId, Timestamp);