---
description: Tenant setup, retry and delete: failure handling, settle semantics, capability keys, DistClientDto, MongoDB user healing.
applies_to: src/Infrastructure/Services/**, src/Infrastructure/Consumers/**, src/Infrastructure/Initialization/**, src/Contracts/DistributionEventHub/Commands/**
---

# Tenant setup and lifecycle

## Tenant Setup — Failure Handling

`DefaultConfigurationCreatorServiceBase.SetupAsync` is the entry point every service uses to provision a
tenant (driven by `PosCreateTenant` / `PosUpdateTenant` and by the startup loop). A setup that throws
used to be logged and forgotten, which left the tenant half-provisioned until the pod restarted — for
Identity, which owns the roles/groups seed, that meant no administrator could be provisioned at all.

Two mechanisms now prevent that:

**Stale MongoDB connection pools are dropped with the tenant.** Dropping a tenant drops its database
user, which invalidates the authentication of every connection already open in this process's cached
client for that database — and the driver never re-authenticates an existing connection. A tenant
re-created under the same name would inherit a pool that can only answer MongoDB error 13
(`"... requires authentication"`), which was the root cause of half-provisioned re-created tenants. `PreUpdatePreDeleteTenantConsumer`
therefore calls `ISystemContext.InvalidateTenantRepositoryClientsAsync` on `PreDeleteTenant` (while the
tenant record still exists, so the database name resolves), and `PosCreatePosUpdateTenantConsumer`
repeats it on `PosCreateTenant` before running setup, which closes the window where a resolve between
the two re-populated the cache. Invalidation **evicts without disposing** (the evicted client may still
be held by live tenant contexts) and is deliberately **not** called on `PosUpdateTenant`: that event
fires on every CK model import and drops no database user — the first iteration of this fix invalidated
(and disposed) there, which made every sequential CK batch import (FixAll) fail from the second model
on with `ObjectDisposedException('CoreServerSessionPool')`.

**One broken tenant no longer blocks startup.** `DefaultConfigurationInitializationService` guards each
child tenant's `SetupAsync` individually: a failure is logged (and durably recorded, see below) and the
other tenants are unaffected, instead of aborting and failing the host start for every remaining tenant.
The **system** tenant is deliberately still fatal — without it nothing works, so a broken instance must
not come up looking healthy.

**Child-tenant setup runs in parallel.** The child-tenant loop is a `Parallel.ForEachAsync` bounded at
`Math.Min(Environment.ProcessorCount, 8)` — the same cap `DefaultConfigurationCreatorServiceStandardized.
DeferredTenantStartParallelism` uses for the deferred-start lane. The dominant cold-start cost is the
per-tenant CK model migrations + blueprint applies; on a fleet with ~20 tenants running these serially
took minutes (observed ~6 min on test-2 when the identity pod was additionally CPU-throttled), which
delayed the distribution event hub — it starts only after tenant init — long enough that downstream
services (e.g. communication-controller) timed out their `CreateIdentityDataCommandRequest` and failed
their rolling deploy. Parallelizing is safe because each tenant's setup is independent per-tenant work
(its own repository, session and CK cache entry — `CkCacheService` is a per-tenant-keyed
`ConcurrentDictionary`); the only process-shared state on the path is the three deferred/pending tenant
collections in the Standardized creator, which are now guarded by a single `_deferredCollectionsLock`
(taken for every mutation and snapshot, never held across an `await`). The system tenant is still set up
first and sequentially, since child setup may depend on it.

**Durable per-service retry.** `SetupAsync` records a failure in `ITenantSetupRetryStore`
(`octo-construction-kit-engine-mongodb`, keyed by `ServiceId` + tenant) and clears the entry on success.
`RetryFailedTenantsAsync` — driven every 30 s by `FailedTenantRetryLoop` (started by `HostedInitializer`, see below), which every
service already registers — claims one pending tenant at a time behind a Mongo lease and re-runs
`SetupAsync`, up to 10 attempts with a 60 s minimum interval. Wiring is opt-in per service: pass
`tenantSetupRetryStore` to the base constructor (Identity and Asset-Repo do). Services that do not are
unchanged, because the whole path is a no-op when the store is null.

> A subclass that overrides `RetryFailedTenantsAsync` **must** call `base.RetryFailedTenantsAsync()` or
> `RetryPendingTenantSetupsAsync()`, otherwise its queue is never drained.
> `DefaultConfigurationCreatorServiceStandardized` does this alongside its own reconcile pass.

**In-memory retry that needs no MongoDB.** The durable store above is optional (only Identity and
Asset-Repo wired it) and best-effort: during a MongoDB primary election its own write times out and is swallowed.
Staging-1, 2026-10-21: the communication controller logged "this tenant will be retried in the background" for
two customer tenants, but wired no store, so nothing was ever retried and their adapters stayed offline until a
manual pod restart. `SetupAsync` therefore **always** queues a failed tenant in `TenantSetupRetryTracker`
(`src/Infrastructure/Services/`, a process-wide singleton; the creator falls back to
`TenantSetupRetryTracker.Default`, so a service benefits **without any wiring** — comm controller and bot-services
pass the DI instance explicitly, Identity/Asset-Repo and the rest inherit it). `RetryPendingTenantSetupsAsync`
(driven every 30 s by `FailedTenantRetryLoop`) first drains this queue, then the durable store.

- **Backoff, no give-up**: 30 s, 60 s, 120 s, 240 s, then 300 s forever (`TenantSetupRetryOptions`, section
  `TenantSetupRetry`: `InitialDelay`, `MaxDelay`, `DegradedAfter`). The only terminal case is a tenant that no
  longer exists (`TenantException.IsTenantNotFound`) or a delete in progress. The durable store keeps its own
  10-attempt budget; the in-memory queue does not.
- **`FailedTenantRegistry`** (deferred-start failures) uses the same backoff and no longer removes a tenant after
  10 retries: reaching the budget is reported **once** (`OnTenantRetriesExhaustedAsync`, error log; the durable
  lifecycle record is deliberately NOT set to Failed, since the retry continues and nothing would reset it) and the retry carries on at the capped interval.
- **Logs** (tenant id, attempt, next delay): `Tenant 'x' is queued for background setup retry: attempt n failed,
  next retry in …`, `Retrying failed setup of tenant 'x' (attempt n)`, and on success `Setup of tenant 'x'
  succeeded after n failed attempt(s)`.
- **Health**: `TenantSetupHealthCheck` (`/health`, check name `TenantSetup`) is `Degraded` when a tenant has been
  failing longer than `DegradedAfter` (default 10 min), naming the tenants. It is **not** tagged `ready`: a service
  that serves its healthy tenants must stay in the load balancer.
- **Metrics** (meter `Meshmakers.Octo.TenantSetup`, registered in `ObservabilityBuilder`): gauges
  `octo.tenant_setup.failed` (1 per failing tenant) and `octo.tenant_setup.pending_seconds` (age of the failure),
  tags `octo.tenant.id` and `octo.tenant_setup.phase` (`setup` | `start`). Series exist only while the tenant is
  failing. The Dash0 alert rule on them lives in `meshmakers-infrastructure`.
- **The retry loop runs during the startup pass.** `FailedTenantRetryLoop` is not a hosted service any more:
  hosted services start sequentially and `HostedInitializer.StartAsync` blocks for the whole pass (all tenants), so a
  loop registered behind it only started once the slowest tenant was done (test-2, 2026-10-22: announced "+30 s",
  actual ~5 min). `HostedInitializer` now starts the loop (if `AddOctoServiceInfrastructure` registered it) **before**
  the initialization services run and stops it in `StopAsync`. The first tick is 30 s after the start, then every 30 s
  (`PeriodicTimer` over `TimeProvider`), so a due retry waits at most for the next tick (30 s after the `not before` time in the
  log line, as long as the previous tick has finished: a tick retries tenants one after the other, so a slow tenant delays
  the rest — unchanged). `StopAsync` waits for a running tick only until the host's shutdown deadline. Consumers need no change. Test: `FailedTenantRetryLoopTests` (fake time, retry due before the pass ends
  runs while the pass is still running).
- **System tenant: fail-fast stays by decision.** If `SetupAsync` of the system tenant (`octosystem`) fails,
  `DefaultConfigurationInitializationService` still throws, `HostedInitializer` fails the host start (and stops the
  retry loop, since the host does not call `StopAsync` of a service whose start failed) and the container restarts.
  Reasons: without the system tenant nothing works (no tenant list, no identity, no CK cache), so there is nothing a
  retrying process could serve meanwhile and a half-started process would answer `/health` with a misleading state;
  Kubernetes backoff heals it reliably (measured on test-2, MongoDB outage 2026-10-22: ~1 min, one restart); an
  in-process retry would have to hold the initialization pass open (and keep the pod not Ready) or continue with
  the remaining initialization services without the system tenant — both riskier than a restart and neither
  adds anything over the container restart. Child tenants are different: their failure never stops the host, they
  are retried in-process (above).
- Tests: `tests/Infrastructure.Tests/Services/TenantSetupRetryTests.cs` (backoff, store down, startup loop with a
  failing tenant that is enabled after MongoDB returns without restart, health, metrics, registry). Tests use one
  isolated `new TenantSetupRetryTracker()` per test (shared across the creator instances of that test when it
  exercises a retry), otherwise they share `Default`; registries get explicit `Options.Create(new TenantSetupRetryOptions())`.

**`Active` requires a real identity seed.** `CheckSetupIdentityDataAsync` used to mark a tenant Active as
soon as `CreateIdentityDataCommandRequest` answered `Success`. That consumer only creates ApiScopes,
ApiResources and Clients — **not roles or groups** — so a tenant whose Identity-side setup never ran was
recorded as fully provisioned while having zero roles (observed live on staging-1). The consumer now
answers `CreateIdentityDataResult.SuccessIdentityDataSeedPending` when the tenant has no roles, and the
creator treats that as a transient not-ready condition: it sets lifecycle phase `IdentityDataPending`
and throws, so the tenant stays `Creating` and every retry path keeps driving it. The enum value is
additive — an older producer never sends it, an older consumer falls into its unexpected-result branch,
which does not mark the tenant provisioned either.

## `DistClientDto` — secrets and roles over the bus

`CreateIdentityDataCommandRequest.Clients` is the only channel a backend service has for creating an
OAuth client: the identity REST API needs an `octo_api` bearer token, i.e. an identity the caller does
not have yet at provisioning time. To let the Communication Controller create the **pipeline service
account** (a `client_credentials` client that must have a secret and roles), `DistClientDto` gained
three optional properties:

| Property | Default | Meaning |
|---|---|---|
| `ClientSecret` | `null` | **Plaintext**. The identity service hashes it (SHA-256, Duende's shared-secret convention) and stores only the hash. `null` means "do not (re-)issue" — the consumer then preserves whatever the existing client has, which is what makes a repeat provisioning run a no-op. |
| `RequireClientSecret` | `false` | Reproduces the value the identity consumer used to hard-code, so every pre-existing producer keeps creating public clients unchanged. |
| `AssignedRoleNames` | `null` | Role names assigned through the identity `AssignedRole` association — additive, idempotent, unknown names skipped with a warning. There was no bus path for client roles before. |

Two rules for anyone touching this DTO:

- 🔴 **Never log a `DistClientDto`.** `ToString()` is **overridden** to print only the client id,
  precisely because the compiler-generated record `ToString()` prints every property — a single
  `logger.LogDebug("{Dto}", dto)` would otherwise write a live client secret into the log pipeline.
  Keep the override if you add properties.
- The properties are additive, so an **older identity service silently ignores them** and would
  create a secretless client. Ship identity before any producer that relies on them.

## Tenant Delete — Settle Semantics

A tenant delete cannot stop events and setups already in flight (broadcast queues are per-instance and
unordered relative to the delete, and `PosUpdateTenant` fires on every CK model import, so provisioning
is an event storm that trails the delete by seconds to minutes). Four mechanisms make deletes converge:

- **`SetupAsync` Deleting gate.** While a durable `Deleting` tombstone exists, setup is skipped
  quietly: no retry row is recorded, and no CK import can lazily re-create the dropped database. The
  lifecycle store is registered platform-wide by `AddMongoDbRuntimeRepository`; a service opts in by
  passing `tenantLifecycleStore` to the base constructor (Identity does; Standardized subclasses
  forward it). Best-effort — a store outage never blocks provisioning.
- **`PosUpdateTenant` echo guard.** `PosCreatePosUpdateTenantConsumer` drops update events whose
  tenant is no longer registered — by definition post-delete echoes. The check is the registry-only
  probe `ISystemContext.IsTenantRegisteredAsync` (no context construction, no resolve-time CK imports
  — PosUpdateTenant fires per CK import, so a full resolve here would double every setup pass's
  resolve work, and it would throw during system-tenant bootstrap where the old flow skipped
  quietly). `PosCreateTenant` is deliberately NOT gated: its record may legitimately not be committed
  yet (see Tenant Setup — Failure Handling above); the durable retry covers that race.
- **`PosUpdateTenant` scope guard.** After the echo guard, the consumer checks
  `PosUpdateTenant.Scope`: a `TenantUpdateScope.CacheOnly` update (the nightly
  `AttributeValueAggregatorJob` in bot-services writing `AutoCompleteValues` onto CK attributes, published
  for every tenant at 00:00Z) only unloads the CK cache and **skips `SetupAsync`**. Before, the setup ran
  for every scope, and in platform-services `RefreshTenantStateAsync` force re-applies the
  `System.TenantMode` blueprint — which recreated `System/TenantModeConfiguration` from the seed every
  night and silently reset the operator's `PublishWorkloadObservability` / `PublishCkModelObservability`
  opt-in (all `octo.workload.*` / `octo.pipeline.*` metrics went dark on prod-1). `Full` — the enum
  default, i.e. what every older publisher deserializes to — is unchanged. Same semantics as the
  communication controller's `TenantManagementConsumer`, which skips the adapter restart relay for
  `CacheOnly`.
- **Terminal not-found classification.** The drain loop treats `TenantException.IsTenantNotFound` as
  terminal and drops the entry instead of hammering a tenant that cannot come back. Safe on the retry
  path: a claim happens ≥ 60 s after the failure was recorded, long after any legitimate create
  transaction committed.
- **Delete settle sweep.** `ReconcileDeletingTenantsAsync` (Standardized reconcile lane; no-op without
  the lifecycle store, i.e. everywhere but asset-repo) completes deletes whose `Deleting` tombstone is
  older than 90 s: it re-drops a shell database a late import resurrected (never one another tenant
  has claimed — checked via `TryGetTenantIdByDatabaseNameAsync`), clears re-seeded retry rows across
  all services, and removes the tombstone, re-opening the tenant id. A tombstone whose tenant is still
  fully registered marks a delete that died before its metadata commit and is rolled back instead —
  crashed deletes converge in both directions. The delete endpoint (asset-repo) writes the settle
  tombstone via `ITenantLifecycleStore.EnsureDeletingAsync` after the drop.

## Tenant Capability Keys and State Reader

Every capability an operator can switch per tenant — Stream Data, Communication, Reporting, AI
Services — is backed by one enabled flag stored as an `RtTenantConfiguration` document **in the
tenant's own database** (`ITenantContext.Set/Get/DeleteConfigurationAsync`). The Standardized creator
writes the flag under the `serviceEnabledKey` its subclass passes in; the middleware's 403 gate and
`IConfigurationService.IsEnabledAsync` read it back. Two things are easy to get wrong when another
service needs that flag:

- **The key literals live here now** — `TenantCapabilityConfigurationKeys` (`Communication`,
  `Reporting`, `AiServices`) in `src/Infrastructure/Services/`. The owning services' `internal`
  constants are copies of these literals and are being switched over to reference this class; do not
  introduce a fourth copy. Stream Data is engine-owned (`StreamDataConfigurationKeys.StreamDataEnabledKey`,
  value type `StreamDataGlobalSettings`) and is deliberately not listed — its disable precondition
  lives in the engine as well (see below).
- **"Disabled" has two shapes.** Communication, Reporting and AI *delete* the document on Disable;
  Stream Data keeps it with `IsEnabled = false`. `ITenantCapabilityStateReader` (registered by
  `AddOctoServiceInfrastructure`) normalises both — missing key or `false` ⇒ disabled — and returns the
  enabled `TenantCapability` values in declaration order. It reads Stream Data through the engine's
  `IsStreamDataEnabledAsync` and the other three through `GetConfigurationAsync`, so it needs no call
  to the owning service. Read failures propagate: a caller that gates a destructive operation on this
  answer must never see an unreadable state as "disabled". The parent/child overload resolves the child
  via `TryGetChildTenantContextAsync` (which runs the resolve-time CK auto-imports) and throws
  `TenantException.TenantDoesNotExist` for anything that is not a direct child.

First consumer: the asset repository's tenant `Delete`/`Detach` refuse with 409 while any capability is
enabled.

### Disable is a verified precondition, not a teardown

`DefaultConfigurationCreatorServiceStandardized.DisableAsync` consults
`protected virtual Task<string?> GetDisableBlockerAsync(tenantId)` **after** the already-disabled check
(disabling twice stays the idempotent "already disabled" answer) and **before** the enabled flag is
removed. A non-null answer is thrown as `ConfigurationException.TenantDisableBlocked(reason)` — the only
`ConfigurationException` with `IsConflict = true`, which the owning service's controller maps to **409**
(`catch (ConfigurationException e) when (e.IsConflict)`) while every other one stays a 400. The
transaction is aborted, the flag stays, `StopTenantAsync` does not run. The reason is surfaced verbatim
to CLI/MCP/Studio, so the override must produce a complete operator message: which resources are still
deployed and how to remove them.

Why a precondition instead of tearing down inside `StopTenantAsync`: the DB deployment state is what the
operator mirrors back (reverse-sync), so checking it before the flag flip *is* confirming the actual end
state; a flag flip that helm-uninstalls a production tenant's workloads would be a dangerous side effect;
and a refusal is remediable through the existing undeploy paths, whereas an automatic cascade silently
no-ops for Edge pools and after a controller restart. The hook answers the Communication and Reporting
requirements in one shape. **Reporting and AI Services never override it**: Reporting owns
no resources outside the tenant database and renders synchronously inside the request; the AI service's
per-tenant worker pod is operator-owned and its sessions/leases are tenant data. Their controllers still
map `IsConflict` to 409 for contract parity, so a future blocker cannot degrade to a 400. A failing read
in an override must throw — an unreadable state is not a torn-down state.

**Stream Data cannot use the hook** — its disable goes through the engine
(`ITenantContext.DisableStreamDataAsync`), never through a Standardized creator — but carries the same
contract: `TenantContext.DisableStreamDataAsync` (octo-construction-kit-engine-mongodb) refuses with
`StreamDataDisableBlockedException` while any archive of the tenant is still `Activated`, naming the
archives, and the asset repository's `StreamDataController` maps that exception to 409 with an
`OperationFailedErrorDto` that appends the `DisableArchive` / `DeleteArchive` remediation. Disabled,
Failed and Created archives never block; the flag flip keeps the model, the entities and the tables. The
engine also drops the CrateDB tables of the tenant's own archives when a tenant is dropped for good
(`DeleteChildTenantMetadataAsync(..., dropStreamData: true)` → `DropTenantDatabaseAsync`, best-effort),
so a Delete leaves no orphaned tables and the guard only has to ensure nothing is live. The deleting
settle sweep here re-drops only the database (its handle carries no archives — the tables were dropped
by the original delete).

## The retry loop re-ensures the tenant's MongoDB user

`FailedTenantRetryLoop` calls `ISystemContext.EnsureTenantDatabaseUserAsync` for every tenant whose retry is due
(in-memory tracker and deferred-start registry) **before** `RetryFailedTenantsAsync`. A tenant whose MongoDB user is
gone failed its setup with the same `MongoAuthenticationException` on every attempt (617 attempts in 7 days on a local
stack) because the user is created only on tenant create/attach; one retry cycle now re-creates it and the setup
completes. The call is idempotent and best-effort: a service without `ISystemContext` or without admin credentials
logs a warning and retries as before. The degraded `TenantSetup` health check keeps naming the failing tenant.
Test: `FailedTenantRetryLoopDatabaseUserTests`.

### Healing a RUNNING tenant

`TenantDatabaseUserHealer` (`Services/`) is the shared helper for a user that vanishes under a tenant that already runs:
`TryHealAsync(systemContext, tenantId, logger)` calls `EnsureTenantDatabaseUserAsync` at most once a minute per tenant
(process-wide `TenantDatabaseUserHealer.Default`), never throws and returns `null` when skipped or failed;
`IsAuthenticationFailure(ex)` recognises MongoDB error 18/13 (also inside write/bulk-write/inner/aggregate exceptions)
and failed SCRAM authentication. `ReconcileStalledTenantsAsync` heals before an attempt counts and, when the budget was
spent on a missing user, requeues the tenant (`RequeueForReconcileAsync`) instead of marking it Failed. identity-services
uses the same helper in its `TenantProbeGuard`. Tests: `TenantDatabaseUserHealerTests`,
`DefaultConfigurationCreatorServiceStandardizedTests.Reconciler_*`.
