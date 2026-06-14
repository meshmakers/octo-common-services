using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Base class for every backend service's default-configuration creator. Owns the
///     <c>SetupAsync</c> entry point, the lifecycle-only <see cref="RefreshTenantStateAsync"/>
///     hook, and — since Phase 3 of the platform-services initiative — the service-managed
///     blueprint apply loop (<see cref="ApplyServiceManagedBlueprintsAsync"/>) that
///     <see cref="DefaultConfigurationCreatorServiceStandardized"/> used to own exclusively.
///     The lift lets services on <c>Base</c> (today: Identity) use the same blueprint pattern
///     as Communication-Controller / Admin-Panel without first migrating to <c>Standardized</c>.
/// </summary>
public abstract class DefaultConfigurationCreatorServiceBase(
    ILogger<DefaultConfigurationCreatorServiceBase> logger,
    IBlueprintService? blueprintService = null,
    IEnumerable<IBlueprintEmbeddedSource>? embeddedBlueprintSources = null)
    : IDefaultConfigurationCreatorService
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    /// <inheritdoc />
    public bool DeferTenantStart { get; set; }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task SetupAsync(string tenantId)
    {
        logger.LogInformation("Setup tenant: '{TenantId}'", tenantId);

        if (!TenantsInHandling.TryAdd(tenantId, true))
        {
            logger.LogWarning("Setup tenant already in work: '{TenantId}'", tenantId);
            return;
        }

        try
        {
            await SetupTenantAsync(tenantId).ConfigureAwait(false);

            // Phase 2 of the platform-services initiative — lifecycle-only refresh hook.
            // `DeferTenantStart` is true during the cold-start initialization loop, false
            // during attach / restore / manual Enable. Skipping the refresh on cold-start
            // avoids the failure mode where a pod restart resets every tenant's runtime
            // state on a service that uses the hook for force-re-apply work (see the
            // admin-panel `System.TenantMode` runbook for why this gate matters). On
            // attach / restore / Enable the tenant has either just arrived (no install row
            // for the current cluster's helm vars) or just transitioned, so a force pass
            // is the safe default.
            if (!DeferTenantStart)
            {
                await RefreshTenantStateAsync(tenantId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup tenant failed: '{TenantId}'", tenantId);
            throw;
        }
        finally
        {
            TenantsInHandling.Remove(tenantId, out _);
            logger.LogInformation("Setup tenant handling done: '{TenantId}'", tenantId);
        }
    }

    /// <inheritdoc />
    public virtual Task StartDeferredTenantsAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task RetryFailedTenantsAsync()
    {
        return Task.CompletedTask;
    }

    protected abstract Task SetupTenantAsync(string tenantId);

    /// <summary>
    ///     Hook for tenant-online refresh logic that must NOT fire during the cold-start
    ///     initialization loop. Called from <see cref="SetupAsync"/> after
    ///     <see cref="SetupTenantAsync"/> completes, only when
    ///     <see cref="IDefaultConfigurationCreatorService.DeferTenantStart"/> is false —
    ///     i.e. on attach / restore / manual <see cref="IConfigurationService.EnableAsync"/>
    ///     (the Standardized base also invokes the hook at the tail of its
    ///     <c>EnableAsync</c> path; see <see cref="DefaultConfigurationCreatorServiceStandardized.EnableAsync"/>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Default no-op — services opt in by overriding with their tenant-online refresh
    ///         logic. Admin-Panel's <c>RefreshTenantModeAsync</c> is the proven example: it
    ///         force-re-applies the <c>System.TenantMode</c> blueprint so a tenant restored
    ///         from prod-1 onto test-2 lands the test-2 EnvironmentMode value instead of the
    ///         source cluster's Production. Identity's Phase 3 override (see
    ///         <c>octo-platform-services/docs/concepts/phase-3-identity-as-blueprint.md</c>)
    ///         calls <see cref="ApplyServiceManagedBlueprintsAsync"/> with
    ///         <c>throwOnFailure: false</c> on the <c>System.Identity.Bootstrap</c> blueprint
    ///         to force-re-apply the seed entities.
    ///     </para>
    ///     <para>
    ///         Failures should generally be logged but not propagated, mirroring Admin-Panel's
    ///         pattern — at this point the tenant is already operational, a refresh miss is a
    ///         degradation rather than a hard failure. Throwing from the hook bubbles up
    ///         through <see cref="SetupAsync"/> and propagates to the lifecycle-event consumer.
    ///     </para>
    /// </remarks>
    protected virtual Task RefreshTenantStateAsync(string tenantId) => Task.CompletedTask;

    /// <summary>
    ///     Prefix used by the default <see cref="IsServiceManagedBlueprint"/> implementation to recognise
    ///     embedded blueprints this service owns. Override (or set in the derived class via the
    ///     property syntax <c>protected override string? ServiceManagedBlueprintPrefix =&gt; "System.X.";</c>)
    ///     to opt into the service-managed blueprint pattern that auto-applies on tenant Enable and
    ///     startup. The trailing dot keeps the match anchored so unrelated names do not leak in
    ///     — e.g. setting <c>"System.Communication."</c> matches <c>System.Communication.Release-1.5.0</c>
    ///     but not a future <c>System.CommunicationOps-1.0.0</c>.
    /// </summary>
    /// <remarks>
    ///     Set to <c>null</c> by default. When null and <see cref="IsServiceManagedBlueprint"/> is not
    ///     overridden, <see cref="ApplyServiceManagedBlueprintsAsync"/> finds no candidates and the
    ///     loop is a no-op — the safe default for services that do not own blueprints.
    /// </remarks>
    protected virtual string? ServiceManagedBlueprintPrefix => null;

    /// <summary>
    ///     Decides whether a given embedded blueprint is owned by this service and therefore eligible
    ///     for auto-apply by <see cref="ApplyServiceManagedBlueprintsAsync"/>. The default implementation
    ///     matches when <paramref name="blueprintId"/>'s <see cref="BlueprintId.Name"/> starts with
    ///     <see cref="ServiceManagedBlueprintPrefix"/> (ordinal compare). Override when the service
    ///     also owns one or more blueprints outside its prefix — e.g. Admin Panel uses
    ///     <c>System.UI.</c> as its prefix but additionally owns the cross-cluster
    ///     <c>System.TenantMode</c> blueprint that does not fit the namespace.
    /// </summary>
    protected virtual bool IsServiceManagedBlueprint(BlueprintId blueprintId)
    {
        return !string.IsNullOrEmpty(ServiceManagedBlueprintPrefix)
               && blueprintId.Name.StartsWith(ServiceManagedBlueprintPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Applies (or re-applies) every embedded blueprint matching <see cref="IsServiceManagedBlueprint"/>,
    ///     picking the newest registered version per blueprint name. Each blueprint's <c>requires:</c>
    ///     block decides whether it actually applies to the given tenant — non-matching blueprints
    ///     return <see cref="BlueprintApplicationResult.WasSkipped"/>=true, which is logged at debug.
    /// </summary>
    /// <param name="tenantId">Target tenant.</param>
    /// <param name="throwOnFailure">
    ///     When true (the <c>Enable</c> path or initial <c>SetupTenantAsync</c> seed), throws
    ///     <see cref="InitializationException"/> on the first failed blueprint apply. When false
    ///     (the per-tenant startup path / <c>RefreshTenantStateAsync</c>), failures are logged
    ///     and reported via <see cref="OnServiceManagedBlueprintApplyFailedAsync"/> but do not
    ///     stop other blueprints in the same iteration — startup continues so the tenant can
    ///     still serve traffic on whichever blueprint version it already has.
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to <see cref="IBlueprintService.ApplyBlueprintAsync"/>.</param>
    /// <remarks>
    ///     If <see cref="IBlueprintService"/> or the embedded source catalog were not supplied to the
    ///     constructor, this method is a silent no-op. Subclasses that opt in via
    ///     <see cref="ServiceManagedBlueprintPrefix"/> must therefore also pass both dependencies through
    ///     the base constructor or the apply loop will never fire.
    /// </remarks>
    protected async Task ApplyServiceManagedBlueprintsAsync(
        string tenantId,
        bool throwOnFailure,
        CancellationToken cancellationToken = default)
    {
        if (blueprintService == null || embeddedBlueprintSources == null)
        {
            return;
        }

        var blueprintsByName = embeddedBlueprintSources
            .Where(s => IsServiceManagedBlueprint(s.BlueprintId))
            .GroupBy(s => s.BlueprintId.Name, StringComparer.Ordinal);

        foreach (var grouping in blueprintsByName)
        {
            var latest = grouping
                .OrderByDescending(s => s.BlueprintId.Version)
                .First();

            var result = await blueprintService
                .ApplyBlueprintAsync(tenantId, latest.BlueprintId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                if (result.WasSkipped)
                {
                    logger.LogDebug(
                        "Service-managed blueprint {BlueprintId} skipped for tenant {TenantId}: {Reason}",
                        latest.BlueprintId.FullName, tenantId, result.SkipReason);
                }
                continue;
            }

            if (throwOnFailure)
            {
                throw InitializationException.ImportCkModelFailed(tenantId,
                    result.OperationResult.GetMessages());
            }

            logger.LogError(
                "Failed to auto-apply service-managed blueprint {BlueprintId} on tenant {TenantId}: {Messages}",
                latest.BlueprintId.FullName, tenantId,
                string.Join("; ", result.OperationResult.GetMessages()));

            await OnServiceManagedBlueprintApplyFailedAsync(
                    tenantId, latest.BlueprintId, result.OperationResult, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Hook for service-specific reporting when a service-managed blueprint auto-apply fails on
    ///     the startup path (<c>throwOnFailure: false</c>). Default no-op — services that need to surface
    ///     the failure to operators (e.g. via a runtime event log) override this hook. Not called on
    ///     the Enable path because the exception thrown by <see cref="ApplyServiceManagedBlueprintsAsync"/>
    ///     already aborts the Enable transaction.
    /// </summary>
    protected virtual Task OnServiceManagedBlueprintApplyFailedAsync(
        string tenantId,
        BlueprintId blueprintId,
        OperationResult operationResult,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
