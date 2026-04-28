namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub when a CkArchive entity transitions between
///     <c>CkArchiveStatus</c> values (Created → Activated, Activated → Disabled, etc.). Concept
///     §14 — emitted by the archive lifecycle service via <c>IArchiveAuditTrail</c>.
/// </summary>
/// <param name="TenantId">Tenant whose archive transitioned.</param>
/// <param name="ArchiveRtId">Runtime id of the CkArchive entity.</param>
/// <param name="FromStatus">Previous status. Stored as the enum's string name so consumers don't need a shared enum.</param>
/// <param name="ToStatus">New status. Stored as the enum's string name.</param>
/// <param name="Reason">Free-form reason or error code (non-null only on transitions to <c>Failed</c>).</param>
/// <param name="CorrelationId">Correlates the event with other events.</param>
/// <param name="Timestamp">Timestamp the event is created.</param>
public record ArchiveStatusTransition(
    string TenantId,
    string ArchiveRtId,
    string FromStatus,
    string ToStatus,
    string? Reason,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
