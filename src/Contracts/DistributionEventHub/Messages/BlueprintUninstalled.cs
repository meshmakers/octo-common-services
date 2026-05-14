namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Distribution event published after a blueprint was uninstalled from a tenant.
/// </summary>
/// <param name="TenantId">Target tenant.</param>
/// <param name="BlueprintId">Fully-qualified blueprint id that was uninstalled.</param>
/// <param name="CascadedDependencyBlueprintIds">Dependencies that were uninstalled together.</param>
/// <param name="CorrelationId">Correlates the event with the operation.</param>
/// <param name="Timestamp">When the operation completed.</param>
public record BlueprintUninstalled(
    string TenantId,
    string BlueprintId,
    IReadOnlyList<string> CascadedDependencyBlueprintIds,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
