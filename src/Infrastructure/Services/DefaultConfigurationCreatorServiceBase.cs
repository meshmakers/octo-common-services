using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
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
    IEnumerable<IBlueprintEmbeddedSource>? embeddedBlueprintSources = null,
    ITenantSetupRetryStore? tenantSetupRetryStore = null,
    ITenantLifecycleStore? tenantLifecycleStore = null)
    : IDefaultConfigurationCreatorService
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    /// <summary>Retry budget for a durably-recorded failed tenant setup before it is left to an operator.</summary>
    private const int MaxSetupAttempts = 10;

    /// <summary>Tenants claimed per <see cref="RetryFailedTenantsAsync"/> tick.</summary>
    private const int MaxSetupRetriesPerTick = 10;

    private static readonly TimeSpan SetupRetryLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinSetupRetryInterval = TimeSpan.FromSeconds(60);

    // Per-instance owner id for the durable retry lease, so it stays single-flight across service
    // instances / pods even though the creator service itself is transient / scoped (AB#4690).
    private readonly string _setupRetryOwnerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>
    ///     Durable tenant-lifecycle store (AB#4348), when the service wires it. Read by the Deleting
    ///     gate in <see cref="SetupAsync"/> and by the Standardized settle sweep (AB#4829); null keeps
    ///     both a no-op.
    /// </summary>
    protected ITenantLifecycleStore? TenantLifecycleStore { get; } = tenantLifecycleStore;

    /// <summary>
    ///     Durable per-service setup-retry queue (AB#4690), when the service wires it. Exposed for the
    ///     Standardized settle sweep, which clears a deleted tenant's re-seeded entries (AB#4829).
    /// </summary>
    protected ITenantSetupRetryStore? TenantSetupRetryStore { get; } = tenantSetupRetryStore;

    /// <inheritdoc />
    public bool DeferTenantStart { get; set; }

    /// <summary>
    ///     Identifies this service in the shared <see cref="ITenantSetupRetryStore"/>, so each service
    ///     retries only its own failed setups. Defaults to the assembly name of the concrete creator, which
    ///     is stable across restarts and readable in the database; override to pin it explicitly.
    /// </summary>
    protected virtual string ServiceId => GetType().Assembly.GetName().Name ?? GetType().FullName!;

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
            // AB#4829: a durable Deleting tombstone means a delete is tearing this tenant down (or its
            // settle period is still running). Setting the tenant up now would fail against the
            // half-deleted tenant, re-record a retry row the delete just cleared, and its CK import
            // could resurrect the dropped database as an empty shell. Skip quietly; the tombstone's
            // settle sweep owns the cleanup, so nothing is recorded or cleared here.
            if (await IsTenantBeingDeletedAsync(tenantId).ConfigureAwait(false))
            {
                logger.LogInformation("Setup of tenant '{TenantId}' skipped: a delete is in progress.",
                    tenantId);
                return;
            }

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

            // Setup completed — drop any durable retry entry from an earlier failure (AB#4690).
            await TryUpdateSetupRetryAsync(tenantId, s => s.ClearAsync(ServiceId, tenantId))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup tenant failed: '{TenantId}'", tenantId);

            // Record the failure durably so RetryFailedTenantsAsync can drive this tenant to completion.
            // Without it, a tenant whose setup fails once (e.g. its database is briefly unreachable right
            // after a delete + recreate under the same name) stays half-provisioned until the service is
            // restarted or an unrelated tenant event happens to arrive (AB#4690).
            await TryUpdateSetupRetryAsync(tenantId,
                s => s.RecordFailureAsync(ServiceId, tenantId, ex.Message)).ConfigureAwait(false);

            throw;
        }
        finally
        {
            TenantsInHandling.Remove(tenantId, out _);
            logger.LogInformation("Setup tenant handling done: '{TenantId}'", tenantId);
        }
    }

    /// <summary>
    ///     Best-effort update of the durable setup-retry queue. A store failure (or the store not being
    ///     wired for this service) must never change the outcome of tenant setup — the queue is recovery
    ///     metadata, not part of the setup itself (AB#4690).
    /// </summary>
    private async Task TryUpdateSetupRetryAsync(string tenantId, Func<ITenantSetupRetryStore, Task> operation)
    {
        if (TenantSetupRetryStore is null)
        {
            return;
        }

        try
        {
            await operation(TenantSetupRetryStore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tenant setup retry store update failed for tenant '{TenantId}'; continuing.",
                tenantId);
        }
    }

    /// <summary>
    ///     Best-effort read of the Deleting tombstone (AB#4829). The lifecycle gate is recovery
    ///     metadata, not part of setup — a store outage must never stop tenants from being
    ///     provisioned, so any failure reads as "not being deleted".
    /// </summary>
    private async Task<bool> IsTenantBeingDeletedAsync(string tenantId)
    {
        if (TenantLifecycleStore is null)
        {
            return false;
        }

        try
        {
            var record = await TenantLifecycleStore.GetAsync(tenantId).ConfigureAwait(false);
            return record is { State: TenantLifecycleState.Deleting };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Tenant lifecycle read failed for tenant '{TenantId}'; proceeding with setup.", tenantId);
            return false;
        }
    }

    /// <summary>
    ///     Drives durably-recorded failed tenant setups to completion (AB#4690). Claims one pending tenant
    ///     of this service at a time behind a Mongo lease and re-runs <see cref="SetupAsync"/>, which clears
    ///     the entry on success and re-records it on failure. Entries that exhausted
    ///     <see cref="MaxSetupAttempts"/> are left in place for an operator instead of being retried
    ///     forever. No-op when the store is not wired.
    /// </summary>
    protected async Task RetryPendingTenantSetupsAsync()
    {
        if (TenantSetupRetryStore is null)
        {
            return;
        }

        for (var processed = 0; processed < MaxSetupRetriesPerTick; processed++)
        {
            TenantSetupRetryRecord? claimed;
            try
            {
                claimed = await TenantSetupRetryStore.TryClaimAsync(ServiceId, _setupRetryOwnerId,
                    SetupRetryLeaseDuration, MinSetupRetryInterval, MaxSetupAttempts).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to claim a tenant for setup retry; will retry next tick.");
                return;
            }

            if (claimed is null)
            {
                // Nothing pending, or everything pending is leased / still inside its retry interval.
                break;
            }

            try
            {
                logger.LogInformation(
                    "Retrying failed setup of tenant '{TenantId}' (attempt {Attempt}/{Max}). Last error: {LastError}",
                    claimed.TenantId, claimed.AttemptCount + 1, MaxSetupAttempts, claimed.LastError);

                await SetupAsync(claimed.TenantId).ConfigureAwait(false);
            }
            catch (TenantException ex) when (ex.IsTenantNotFound)
            {
                // AB#4829: the registry miss is terminal — no retry can ever complete this tenant's
                // setup, and every attempt used to re-record the entry and hammer the just-deleted
                // tenant until the attempt budget was dead. Drop the entry instead. This cannot collide
                // with the PosCreateTenant uncommitted-record race: a claim happens at least
                // MinSetupRetryInterval after the failure was recorded, long after any legitimate
                // create transaction has committed.
                logger.LogInformation(
                    "Dropping setup retry for tenant '{TenantId}': the tenant no longer exists.",
                    claimed.TenantId);
                await TryUpdateSetupRetryAsync(claimed.TenantId,
                    s => s.ClearAsync(ServiceId, claimed.TenantId)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SetupAsync already re-recorded the failure (which also released the lease), so the tenant
                // is picked up again once the retry interval has passed.
                logger.LogWarning(ex,
                    "Setup retry for tenant '{TenantId}' failed; it will be retried after {Interval}.",
                    claimed.TenantId, MinSetupRetryInterval);
            }
        }
    }

    /// <inheritdoc />
    public virtual Task StartDeferredTenantsAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Overriding implementations MUST call <c>base.RetryFailedTenantsAsync()</c> (or
    ///     <see cref="RetryPendingTenantSetupsAsync"/> directly), otherwise the durable setup-retry queue is
    ///     never drained for that service (AB#4690).
    /// </remarks>
    public virtual Task RetryFailedTenantsAsync()
    {
        return RetryPendingTenantSetupsAsync();
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
