namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub when a CkArchive entity is deleted (soft-deleted entity
///     plus dropped CrateDB table). Concept §14 — emitted by the archive lifecycle service via
///     <c>IArchiveAuditTrail</c>.
/// </summary>
/// <param name="TenantId">Tenant whose archive was deleted.</param>
/// <param name="ArchiveRtId">Runtime id of the CkArchive entity.</param>
/// <param name="StatusAtDeletion">CkArchiveStatus the archive held immediately before deletion.</param>
/// <param name="CorrelationId">Correlates the event with other events.</param>
/// <param name="Timestamp">Timestamp the event is created.</param>
public record ArchiveDeleted(
    string TenantId,
    string ArchiveRtId,
    string StatusAtDeletion,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
