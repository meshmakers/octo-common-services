namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Distribution event published after a blueprint was successfully applied to a tenant.
/// </summary>
/// <param name="TenantId">Target tenant.</param>
/// <param name="BlueprintId">Fully-qualified blueprint id (Name-Version).</param>
/// <param name="ApplicationMode">Application mode: Initial, Update, Migration, ReApply.</param>
/// <param name="EntitiesAdded">Entities created during application.</param>
/// <param name="EntitiesUpdated">Entities updated during application.</param>
/// <param name="EntitiesDeleted">Entities deleted during application.</param>
/// <param name="CorrelationId">Correlates the event with the operation.</param>
/// <param name="Timestamp">When the operation completed.</param>
public record BlueprintApplied(
    string TenantId,
    string BlueprintId,
    string ApplicationMode,
    int EntitiesAdded,
    int EntitiesUpdated,
    int EntitiesDeleted,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
