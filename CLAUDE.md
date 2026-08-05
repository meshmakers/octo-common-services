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

## Tests

`tests/Infrastructure.Tests` — xUnit + FakeItEasy. `Infrastructure.csproj` exposes internals to this
assembly via `<InternalsVisibleTo Include="Infrastructure.Tests" />` so internal middleware such as
`TenantMiddleware` can be unit-tested directly (see `Middleware/TenantMiddlewareTests.cs`).
