namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Command to update the CORS clients for a tenant
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
public record CorsClientsUpdate(string TenantId, Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);