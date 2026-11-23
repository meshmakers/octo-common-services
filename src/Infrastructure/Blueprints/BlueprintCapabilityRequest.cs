using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     What a blueprint event says about a tenant: the input of <see cref="IBlueprintCapabilityReconciler" />.
///     Everything in it is a claim of the event and is checked against the tenant before anything is enabled.
/// </summary>
/// <param name="TenantId">Tenant the event names.</param>
/// <param name="BlueprintId">Fully-qualified blueprint id (Name-Version) the event names.</param>
/// <param name="CkModelDependencies">CK model dependencies the event reports; <c>null</c> = not reported.</param>
/// <param name="CorrelationId">Correlation id of the blueprint operation, for the log.</param>
/// <param name="Trigger">Name of the event, for the log (<c>BlueprintApplied</c>, <c>BlueprintUpdated</c>).</param>
public sealed record BlueprintCapabilityRequest(
    string? TenantId,
    string? BlueprintId,
    IReadOnlyList<string>? CkModelDependencies,
    Guid CorrelationId,
    string Trigger)
{
    /// <summary>The request of a <see cref="BlueprintApplied" /> event.</summary>
    public static BlueprintCapabilityRequest From(BlueprintApplied message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new BlueprintCapabilityRequest(message.TenantId, message.BlueprintId, message.CkModelDependencies,
            message.CorrelationId, nameof(BlueprintApplied));
    }

    /// <summary>The request of a <see cref="BlueprintUpdated" /> event.</summary>
    public static BlueprintCapabilityRequest From(BlueprintUpdated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new BlueprintCapabilityRequest(message.TenantId, message.BlueprintId, message.CkModelDependencies,
            message.CorrelationId, nameof(BlueprintUpdated));
    }
}

/// <summary>Result of <see cref="IBlueprintCapabilityReconciler.ReconcileAsync" />.</summary>
public enum BlueprintCapabilityOutcome
{
    /// <summary>The event carries no CK model dependencies (publisher older than this field): nothing done.</summary>
    NoDependencyInformation,

    /// <summary>The blueprint does not need the capability: nothing done.</summary>
    NotRequired,

    /// <summary>
    ///     The event does not match the tenant (unknown tenant, system tenant, blueprint not installed in that
    ///     version, invalid id): nothing done.
    /// </summary>
    Refused,

    /// <summary>The capability was already enabled: nothing done.</summary>
    AlreadyEnabled,

    /// <summary>The capability was enabled.</summary>
    Enabled,

    /// <summary>AI only: Communication was not enabled within the wait; AI was not enabled.</summary>
    PrerequisiteMissing,

    /// <summary>Enabling failed; the cause is logged.</summary>
    Failed
}
