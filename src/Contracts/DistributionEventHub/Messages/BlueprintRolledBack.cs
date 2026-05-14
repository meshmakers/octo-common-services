namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Distribution event published after a tenant was rolled back from a blueprint backup.
/// </summary>
/// <param name="TenantId">Target tenant.</param>
/// <param name="BlueprintId">Blueprint id that was active before the rollback, or null if unknown.</param>
/// <param name="BackupId">Backup identifier that was restored.</param>
/// <param name="CorrelationId">Correlates the event with the operation.</param>
/// <param name="Timestamp">When the operation completed.</param>
public record BlueprintRolledBack(
    string TenantId,
    string? BlueprintId,
    string BackupId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
