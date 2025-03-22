namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Used to signal that identity provider configuration for a tenant is updated.
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
public record IdentityProviderUpdate(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);