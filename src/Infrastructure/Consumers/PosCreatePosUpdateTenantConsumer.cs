using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

// ReSharper disable once ClassNeverInstantiated.Global
internal class PosCreatePosUpdateTenantConsumer(
    ILogger<PosCreatePosUpdateTenantConsumer> logger,
    ICkCacheService ckCacheService,
    ISystemContext systemContext,
    IDefaultConfigurationCreatorService defaultConfigurationCreatorService,
    ITenantGenerationReader? tenantGenerationReader = null,
    IStreamDataRepositoryFactory? streamDataRepositoryFactory = null,
    ITenantLifecycleStore? tenantLifecycleStore = null)
    : IDistributedConsumer<PosCreateTenant>, IDistributedConsumer<PosUpdateTenant>
{
    private static readonly ConcurrentDictionary<string, bool> TenantsInHandling = new();

    /// <summary>
    ///     AB#6247: tenants for which a Full update arrived while the tenant was in work and was dropped. The archive
    ///     re-check of that update is run once more when the running handling ends, so an import that committed after the
    ///     running re-check loaded the model is not missed. Setup itself keeps the existing coalescing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> RevalidationPending = new();

    private void UnloadCacheIfLoaded(string tenantId)
    {
        if (ckCacheService.IsTenantLoaded(tenantId))
        {
            logger.LogInformation("Pos update/create tenant unloading cache: '{TenantId}'", tenantId);
            ckCacheService.Unload(tenantId);
        }
    }

    /// <summary>
    ///     AB#6247 (CK v2 N2): a CK model import re-checks the tenant's archives against Hidden attributes only in the
    ///     importing service, and that check needs a stream-data repository. Identity, the communication controller and
    ///     platform services import System.Identity, System.Communication and System.UI without one, so a model of theirs
    ///     that makes an archived attribute Hidden would keep being archived. Every import publishes PosUpdateTenant
    ///     (scope Full); a service that owns stream data (a registered <see cref="IStreamDataRepositoryFactory" />) runs
    ///     the re-check here. Services without stream data skip it before resolving the tenant context, so the registry-only
    ///     probe above stays the only lookup there. Best-effort: a failure is logged and never fails the setup.
    /// </summary>
    private async Task RevalidateArchiveAccessAsync(string tenantId)
    {
        if (streamDataRepositoryFactory is null)
        {
            return;
        }

        try
        {
            // Like the setup's Deleting gate: resolving a tenant that a delete is tearing down could run the
            // resolve-time CK imports and re-create its dropped database.
            if (tenantLifecycleStore is not null &&
                await tenantLifecycleStore.GetAsync(tenantId).ConfigureAwait(false) is { State: TenantLifecycleState.Deleting })
            {
                logger.LogInformation("Archive re-check skipped, tenant '{TenantId}' is being deleted", tenantId);
                return;
            }

            var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
            if (tenantContext is not null)
            {
                await tenantContext.RevalidateArchiveAccessAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Re-checking the archives of tenant '{TenantId}' against Hidden attributes failed after a tenant update",
                tenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        // AB#8999: a new creation cycle has a new generation; never answer from the cached one.
        tenantGenerationReader?.Invalidate(context.Message.TenantId);

        logger.LogInformation("Pos create tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            logger.LogWarning("Pos update or create tenant already in work: '{TenantId}'", context.Message.TenantId);
            return;
        }

        try
        {
            var tenantId = context.Message.TenantId.NormalizeString();
            UnloadCacheIfLoaded(tenantId);

            // Belt and braces for AB#4690: the delete-side invalidation already drops the cached MongoDB
            // clients of a tenant's database, but a resolve in the short window between that event and the
            // physical drop could have re-populated the cache with connections authenticated as the
            // now-dropped user. Dropping them again here — before any setup work touches the database —
            // makes a re-created tenant independent of that timing. Best-effort: on PosCreateTenant the
            // tenant record may not be committed yet, in which case the database name cannot be resolved
            // and the (now durable) setup retry covers the next attempt.
            await systemContext.InvalidateTenantRepositoryClientsAsync(tenantId).ConfigureAwait(false);

            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            // An update dropped while the create was in work is re-checked here too (AB#6247).
            if (RevalidationPending.TryRemove(context.Message.TenantId, out _))
            {
                await RevalidateArchiveAccessAsync(context.Message.TenantId.NormalizeString()).ConfigureAwait(false);
            }

            logger.LogInformation("Pos create tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        // AB#8999: a new creation cycle has a new generation; never answer from the cached one.
        tenantGenerationReader?.Invalidate(context.Message.TenantId);

        logger.LogInformation("Pos update tenant received: '{TenantId}'", context.Message.TenantId);

        if (!TenantsInHandling.TryAdd(context.Message.TenantId, true))
        {
            logger.LogWarning("Pos update or create tenant already in work: '{TenantId}'", context.Message.TenantId);
            if (streamDataRepositoryFactory is not null && context.Message.Scope != TenantUpdateScope.CacheOnly)
            {
                RevalidationPending[context.Message.TenantId] = true;
            }

            return;
        }

        try
        {
            var tenantId = context.Message.TenantId.NormalizeString();
            UnloadCacheIfLoaded(tenantId);

            // AB#4829: PosUpdateTenant fires on every CK model import, so a tenant delete regularly
            // races a tail of update events still queued per service instance. An update for a tenant
            // that is no longer registered is by definition such a post-delete echo — running setup
            // would only fail with "does not exist", re-record a durable retry row the delete just
            // cleared, and add three bus-retry rounds of error-level noise. Unlike PosCreateTenant
            // above (whose record may legitimately not be committed yet), dropping it is always safe.
            // Registry-only probe, deliberately NOT TryFindTenantContextAsync: that builds a tenant
            // context and runs the resolve-time CK imports per event, and it throws (instead of the
            // old quiet skip) while the system tenant is still bootstrapping.
            if (!await systemContext.IsTenantRegisteredAsync(tenantId).ConfigureAwait(false))
            {
                logger.LogInformation("Pos update tenant skipped, tenant is no longer registered: '{TenantId}'",
                    context.Message.TenantId);
                return;
            }

            // AB#5497: a CacheOnly update (AB#4895) — e.g. the nightly autocomplete aggregation in
            // bot-services writing AutoCompleteValues onto CK attributes — changed cached tenant/CK-model
            // state only. The cache unload above is the whole reaction it asks for. Running SetupAsync
            // for it re-applied the service-managed blueprints with force (platform-services:
            // RefreshTenantStateAsync), which recreated System/TenantModeConfiguration from the seed
            // every night and silently reset the operator's observability opt-in. Mirrors the
            // communication controller's TenantManagementConsumer, which skips the adapter restart
            // relay for CacheOnly. Full (and any other scope, incl. older publishers that
            // deserialize to Full) keeps running setup.
            if (context.Message.Scope == TenantUpdateScope.CacheOnly)
            {
                logger.LogInformation(
                    "Pos update tenant is cache-only, skipping setup: '{TenantId}'", context.Message.TenantId);
                return;
            }

            // Deliberately NO InvalidateTenantRepositoryClientsAsync here (unlike PosCreateTenant):
            // a tenant update drops no database user, so there is nothing to re-authenticate — and
            // PosUpdateTenant fires on every CK model import, so evicting here would churn a fresh
            // MongoDB client (new connection pool) per imported model for no benefit. The first
            // AB#4690 iteration invalidated (and disposed) here, which broke sequential CK batch
            // imports (FixAll): the event fired by batch step N disposed the client under batch
            // step N+1 — ObjectDisposedException('CoreServerSessionPool').
            await defaultConfigurationCreatorService.SetupAsync(context.Message.TenantId).ConfigureAwait(false);

            await RevalidateArchiveAccessAsync(tenantId).ConfigureAwait(false);
        }
        finally
        {
            TenantsInHandling.Remove(context.Message.TenantId, out _);
            // Released first, then the pending flag is read: an update that is dropped from now on handles itself, one
            // dropped before has set the flag.
            if (RevalidationPending.TryRemove(context.Message.TenantId, out _))
            {
                logger.LogInformation("Re-checking archives of tenant '{TenantId}' for an update dropped while it was in work",
                    context.Message.TenantId);
                await RevalidateArchiveAccessAsync(context.Message.TenantId.NormalizeString()).ConfigureAwait(false);
            }

            logger.LogInformation("Pos update tenant handling done: '{TenantId}'", context.Message.TenantId);
        }
    }
}