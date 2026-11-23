namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     Switches on the capability of the hosting service for a tenant when a blueprint installed in that tenant needs
///     it (the blueprint stays declarative, every service reconciles what it owns on
///     <c>BlueprintApplied</c> / <c>BlueprintUpdated</c>).
/// </summary>
/// <remarks>
///     <para>Invariant: the capability is enabled only</para>
///     <list type="number">
///         <item>for the tenant the event names, after it resolves to an existing tenant other than the system tenant;</item>
///         <item>when the blueprint the event names is installed in that tenant in exactly that version;</item>
///         <item>when the CK model dependencies the event reports contain the model of the capability
///         (<see cref="BlueprintCapabilityRequirements" />); no dependencies reported = nothing (fail closed);</item>
///         <item>AI only after Communication is enabled - it never enables Communication itself.</item>
///     </list>
///     <para>
///         It never disables anything, never throws (a failure is logged with tenant and cause; the blueprint
///         operation is already committed and stays as it is) and is idempotent: an enabled capability is left alone.
///     </para>
/// </remarks>
public interface IBlueprintCapabilityReconciler
{
    /// <summary>
    ///     Enables <paramref name="capability" /> for the tenant of <paramref name="request" /> when the invariant holds.
    ///     <paramref name="capability" /> must be the capability of the hosting service (its
    ///     <c>IConfigurationService</c> is the one that is enabled).
    /// </summary>
    Task<BlueprintCapabilityOutcome> ReconcileAsync(BlueprintCapabilityRequest request,
        BlueprintCapability capability, CancellationToken cancellationToken = default);
}
