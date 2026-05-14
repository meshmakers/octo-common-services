namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Distribution event published after a tenant's blueprint was updated to a new version.
/// </summary>
/// <param name="TenantId">Target tenant.</param>
/// <param name="BlueprintId">Fully-qualified target blueprint id.</param>
/// <param name="FromVersion">Previous blueprint id, or null if none.</param>
/// <param name="UpdateMode">Update mode: Safe, Merge, Full, Migration.</param>
/// <param name="EntitiesAdded">Entities created during update.</param>
/// <param name="EntitiesUpdated">Entities updated during update.</param>
/// <param name="EntitiesDeleted">Entities deleted during update.</param>
/// <param name="BackupId">Backup created before the update, if any.</param>
/// <param name="CorrelationId">Correlates the event with the operation.</param>
/// <param name="Timestamp">When the operation completed.</param>
public record BlueprintUpdated(
    string TenantId,
    string BlueprintId,
    string? FromVersion,
    string UpdateMode,
    int EntitiesAdded,
    int EntitiesUpdated,
    int EntitiesDeleted,
    string? BackupId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
