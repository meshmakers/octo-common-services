namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub before a tenant gets modified
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param>
/// <param name="Scope">
///     How invasive the update is (AB#4895). Optional — messages from older publishers
///     deserialize to <see cref="TenantUpdateScope.Full" />.
/// </param>
public record PreUpdateTenant(string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    TenantUpdateScope Scope = TenantUpdateScope.Full) : EventBase(CorrelationId, Timestamp);