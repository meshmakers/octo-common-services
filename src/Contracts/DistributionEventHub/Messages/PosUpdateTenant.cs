namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub after a tenant got modified
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param>
/// <param name="Scope">
///     How invasive the update is (AB#4895). Optional — messages from older publishers
///     deserialize to <see cref="TenantUpdateScope.Full" />.
/// </param>
public record PosUpdateTenant(string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    TenantUpdateScope Scope = TenantUpdateScope.Full) : EventBase(CorrelationId, Timestamp);