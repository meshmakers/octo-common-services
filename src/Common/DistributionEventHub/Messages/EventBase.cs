namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Base record for all events
/// </summary>
/// <param name="CorrelationId">Guid to correlate events</param>
/// <param name="Timestamp">Timestamp of the event</param>
public record EventBase(Guid CorrelationId, DateTime Timestamp);
