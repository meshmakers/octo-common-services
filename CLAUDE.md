# CLAUDE.md — octo-common-services

Shared .NET infrastructure libraries used across all OctoMesh backend services. See `README.md`
for the published package list. Targets .NET 10, DebugL for local development.

## Build / Test

```bash
dotnet build Octo.Common.Services.sln -c DebugL
dotnet test  Octo.Common.Services.sln -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Propagate the produced `Meshmakers.Octo.Services.*.999.0.0.nupkg` files from `bin/DebugL/` into
`../nuget` so downstream services (Asset-Repo, Reporting, Identity, ...) pick up local changes.

## Multi-Tenancy Middleware

`UseOctoTenants()` (`src/Infrastructure/Configuration/ApplicationBuilderExtensions.cs`) registers
`TenantMiddleware` (`src/Infrastructure/Middleware/TenantMiddleware.cs`). For every tenant-scoped
route it resolves the tenant repository (404 if the tenant does not exist) and stores it in
`context.Items`. When the hosting feature `CanBeEnabled()` and is NOT enabled for the tenant, the
middleware short-circuits with **403 Forbidden**.

### Enabled-gate exemptions (AB#4287)

The 403 enabled-gate is skipped for:

- The exact `SystemEndpoints` paths: `/system`, `/signin-oidc`, `/healthz`.
- **Feature lifecycle endpoints** — any path ending in `/enable` or `/disable`
  (case-insensitive). These endpoints manage the enabled-state itself, so gating them would make a
  disabled feature impossible to re-enable via its own API (the AB#4287 regression: tenant-scoped
  `POST {tenantId}/v1/reporting/enable` returned 403 once reporting was disabled). Tenant resolution
  (404-if-missing + `context.Items`) still runs for these paths — only the enabled-check is skipped.

This relaxation is safe for all consumers: the gate only fires when `CanBeEnabled()` is `true`, and
services with no toggleable feature (e.g. Identity Services) never define `/enable` `/disable`
tenant routes, so their behaviour is unchanged.

## Tenant Setup — Failure Handling (AB#4690)

`DefaultConfigurationCreatorServiceBase.SetupAsync` is the entry point every service uses to provision a
tenant (driven by `PosCreateTenant` / `PosUpdateTenant` and by the startup loop). A setup that throws
used to be logged and forgotten, which left the tenant half-provisioned until the pod restarted — for
Identity, which owns the roles/groups seed, that meant no administrator could be provisioned at all.

Two mechanisms now prevent that:

**Stale MongoDB connection pools are dropped with the tenant.** Dropping a tenant drops its database
user, which invalidates the authentication of every connection already open in this process's cached
client for that database — and the driver never re-authenticates an existing connection. A tenant
re-created under the same name would inherit a pool that can only answer MongoDB error 13
(`"... requires authentication"`), which is the root cause of AB#4690. `PreUpdatePreDeleteTenantConsumer`
therefore calls `ISystemContext.InvalidateTenantRepositoryClientsAsync` on `PreDeleteTenant` (while the
tenant record still exists, so the database name resolves), and `PosCreatePosUpdateTenantConsumer`
repeats it on `PosCreateTenant` before running setup, which closes the window where a resolve between
the two re-populated the cache. Invalidation **evicts without disposing** (the evicted client may still
be held by live tenant contexts) and is deliberately **not** called on `PosUpdateTenant`: that event
fires on every CK model import and drops no database user — the first AB#4690 iteration invalidated
(and disposed) there, which made every sequential CK batch import (FixAll) fail from the second model
on with `ObjectDisposedException('CoreServerSessionPool')`.

**One broken tenant no longer blocks startup.** `DefaultConfigurationInitializationService` guards each
child tenant's `SetupAsync` individually: a failure is logged (and durably recorded, see below) and the
loop continues, instead of aborting and failing the host start for every remaining tenant. The **system**
tenant is deliberately still fatal — without it nothing works, so a broken instance must not come up
looking healthy.

**Durable per-service retry.** `SetupAsync` records a failure in `ITenantSetupRetryStore`
(`octo-construction-kit-engine-mongodb`, keyed by `ServiceId` + tenant) and clears the entry on success.
`RetryFailedTenantsAsync` — driven every 30 s by `FailedTenantRetryBackgroundService`, which every
service already registers — claims one pending tenant at a time behind a Mongo lease and re-runs
`SetupAsync`, up to 10 attempts with a 60 s minimum interval. Wiring is opt-in per service: pass
`tenantSetupRetryStore` to the base constructor (Identity and Asset-Repo do). Services that do not are
unchanged, because the whole path is a no-op when the store is null.

> A subclass that overrides `RetryFailedTenantsAsync` **must** call `base.RetryFailedTenantsAsync()` or
> `RetryPendingTenantSetupsAsync()`, otherwise its queue is never drained.
> `DefaultConfigurationCreatorServiceStandardized` does this alongside its own reconcile pass.

**`Active` requires a real identity seed.** `CheckSetupIdentityDataAsync` used to mark a tenant Active as
soon as `CreateIdentityDataCommandRequest` answered `Success`. That consumer only creates ApiScopes,
ApiResources and Clients — **not roles or groups** — so a tenant whose Identity-side setup never ran was
recorded as fully provisioned while having zero roles (observed live on staging-1). The consumer now
answers `CreateIdentityDataResult.SuccessIdentityDataSeedPending` when the tenant has no roles, and the
creator treats that as a transient not-ready condition: it sets lifecycle phase `IdentityDataPending`
and throws, so the tenant stays `Creating` and every retry path keeps driving it. The enum value is
additive — an older producer never sends it, an older consumer falls into its unexpected-result branch,
which does not mark the tenant provisioned either.

## Tenant Delete — Settle Semantics (AB#4829)

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
  yet (see AB#4690 above); the durable retry covers that race.
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

## Tenant Capability Keys and State Reader (AB#4255)

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
enabled (AB#4255 step 1).

### Disable is a verified precondition, not a teardown (AB#4255 step 2)

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
requirements of AB#4255 in one shape. **Reporting and AI Services never override it**: Reporting owns
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
engine also drops the tenant's CrateDB namespace together with the tenant database
(`TenantContext.DropTenantDatabaseAsync`, best-effort), so a Delete leaves no orphaned schema and the
guard only has to ensure nothing is live.

## Tests

`tests/Infrastructure.Tests` — xUnit + FakeItEasy. `Infrastructure.csproj` exposes internals to this
assembly via `<InternalsVisibleTo Include="Infrastructure.Tests" />` so internal middleware such as
`TenantMiddleware` can be unit-tested directly (see `Middleware/TenantMiddlewareTests.cs`).
