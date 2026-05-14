namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Distribution event published when any blueprint lifecycle operation fails.
/// </summary>
/// <param name="TenantId">Target tenant.</param>
/// <param name="BlueprintId">Blueprint involved, or null if the failure happened before identification.</param>
/// <param name="Operation">Operation name: "Apply", "Update", "Rollback", "Uninstall".</param>
/// <param name="ErrorMessage">Human-readable error description.</param>
/// <param name="CorrelationId">Correlates the event with the operation.</param>
/// <param name="Timestamp">When the failure occurred.</param>
public record BlueprintOperationFailed(
    string TenantId,
    string? BlueprintId,
    string Operation,
    string ErrorMessage,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
