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
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp)
{
    /// <summary>
    ///     Names of the CK models the target blueprint version depends on, distinct and sorted ordinally, as the blueprint
    ///     engine computed them. Services switch on the capability a blueprint needs from it
    ///     (<c>System.Communication</c>, <c>System.Ai</c>).
    /// </summary>
    /// <remarks>
    ///     Optional and additive: <c>null</c> when the publisher does not report it (older versions). A consumer must
    ///     then not derive a capability from the event (fail closed), and must never derive it from the CK models a
    ///     tenant holds instead: System.Communication is imported into every tenant. An init-only property, so the
    ///     positional constructor and the wire format stay compatible with older publishers and consumers (an unknown
    ///     JSON property is ignored, a missing one stays <c>null</c>).
    /// </remarks>
    public IReadOnlyList<string>? CkModelDependencies { get; init; }
}
