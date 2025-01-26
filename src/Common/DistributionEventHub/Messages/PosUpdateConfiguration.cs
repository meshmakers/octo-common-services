namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Signals that a configuration has been updated
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="ConfigurationName">Configuration name that is updated</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
public record PosUpdateConfiguration(
    string TenantId,
    string ConfigurationName,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);