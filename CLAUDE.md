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

## Tenant Authorization — the service-token exemption (AB#5032)

`UseOctoTenantAuthorization()` registers `TenantAuthorizationMiddleware`
(`src/Infrastructure/Middleware/TenantAuthorizationMiddleware.cs`), which validates the route tenant
against the caller's `tenant_id` claim. It used to **skip that check entirely for any token without a
`sub` claim** — i.e. for every client-credentials token — because such a token carried nothing to
check against. Combined with `ValidateAudience = false` in asset-repo / platform-services / MCP, that
meant **any** client-credentials client of the authority passed the transport gate and could then
address **any** tenant, not just the two components that need it (the AI adapter worker and — via
`/{tenantId}/mcp` — the mesh adapter's `AnthropicAiQueryNode`). Client mirroring
(`AutoProvisionInChildTenants`) makes it worse: one clientId/secret pair is valid instance-wide.

Two halves close it, and they must ship in this order:

1. **Identity stamps the tenant.** `ClientCredentialsRoleTokenValidator` (octo-identity-services) now
   adds an unprefixed `tenant_id` claim to every `client_credentials` token, taken from the tenant the
   token request resolved to (`acr_values=tenant:X`, falling back to the system tenant, which is the
   directory the client store actually resolved the client from).
2. **This middleware narrows the exemption**, staged behind
   `TenantAuthorizationOptions.ServiceTokenEnforcement`
   (`src/Infrastructure/Configuration/TenantAuthorizationOptions.cs`):

| Mode | Behaviour |
|---|---|
| `Disabled` | Pre-AB#5032: service tokens are never checked, nothing is logged. |
| `LogOnly` (**default**) | Request outcomes identical to before, but every access an enforcing run would refuse is logged as a warning naming the **client id, the token tenant and the route tenant**. This log is the consumer inventory. |
| `Enforce` | A service token may address only its own `tenant_id`, or a tenant it is allow-listed for. Everything else → **403**, including a token with no `tenant_id` at all (fail closed). |

Bind it with `services.AddOctoTenantAuthorization(configuration)` (section `TenantAuthorization`, i.e.
`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` / `…__CROSSTENANTSERVICECLIENTIDS__0`). The call is
optional in the compiler's sense only — without it the defaults apply, so a consumer that never wires
it keeps today's behaviour, but its enforcement mode is then **not settable at all**: the environment
variable is inert and the service stays on `LogOnly` while the rest of the estate moves to `Enforce`.

**Every host of the middleware must call both halves.** As of AB#5047 all five do — the `Use…`/`Add…`
pairs are:

| Service | `UseOctoTenantAuthorization()` | `AddOctoTenantAuthorization(builder.Configuration)` |
|---|---|---|
| octo-identity-services | `Program.cs` | `Program.cs` (AB#5032) |
| octo-communication-controller-services | `Program.cs` | `Program.cs` (AB#5032) |
| octo-asset-repo-services | `Configuration/OctoApplicationBuilderExtensions.cs` | `Program.cs` (AB#5047) |
| octo-bot-services | `Program.cs` | `Program.cs` (AB#5047) |
| octo-mcp-service | `Program.cs` | `Program.cs` (AB#5047) |

They all pass `builder.Configuration` into the same helper, so the section name (a `const` here) and
the `OCTO_` env prefix every service registers cannot drift apart — a per-service section name would
be exactly the silent outlier this inventory exists to prevent. When adding a sixth host, add both
calls and a row here; `grep -rn "UseOctoTenantAuthorization" --include='*.cs'` across the checkout is
the audit.

`tests/Infrastructure.Tests/Configuration/TenantAuthorizationOptionsBindingTests.cs` pins the two
properties a fleet-wide switch depends on: the unregistered default is `LogOnly` with an empty
allow-list, and the section binds from both the config section and
`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT`.

**The `tenant_id` match is the mechanism; the allow-list is expected to stay empty.** Both consumers
the exemption was believed to exist for already mint **tenant-bound** tokens, verified in code:

| Consumer | Where | Evidence |
|---|---|---|
| AI adapter worker | `octo-ai-services` `Services/Mcp/McpTokenIssuer.AcquireAccessTokenAsync` | `request.Parameters.Add("acr_values", $"tenant:{tenantId}")`, and the token is **cached per tenant** (`_cache[tenantId]`). `AgentWorkspaceMaterializer` mints it with the session's tenant and writes it into that session's `.mcp.json`, which then talks to `/{tenantId}/mcp`. |
| Mesh adapter | `octo-mesh-adapter` `Services/ServiceAccountTokenService` (3 call sites: service token, delegated token, service-account identity) | `acr_values=tenant:{configuration.TenantId}` from the adapter's own `ServiceAccountConfiguration`. `AnthropicAiQueryNode` calls `{mcpServerUrl}/{etlContext.TenantId}/mcp` with that token — its own tenant. |

So once identity stamps `tenant_id`, both pass the match on their own and need no entry.
`CrossTenantServiceClientIds` remains only as an escape hatch for a service that genuinely fans out
across tenants with one token — none is known today. It is **configuration, not a hard-coded list**
(client ids differ per environment and a hard-coded list would need a release to change); entries
match case-insensitively and a trailing `*` matches a prefix.
🔴 Never list the per-tenant pipeline service accounts (`octo-pipeline-sa-*`) there — they belong to
exactly one tenant. An allow-list entry is a permanent hole, not a migration aid: use `LogOnly` for
the migration.
- The service path accepts **only** `tenant_id` and the allow-list, never `allowed_tenants` — same
  stance as the user path: `allowed_tenants` is a tenant-*selection* hint, not an API authorization.
  (It is user-only anyway; `AllowedTenantsResolver` has no client overload.)
- Before flipping an environment to `Enforce`, read the log. A CLI/CI client that logs in without
  `acr_values` gets `tenant_id = <system tenant>` and will be refused on child-tenant routes — the fix
  is to pass the tenant at login, which `octo-cli LogInClientCredentials` already does per context.

Tests: `tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareTests.cs` (match allowed in
every mode, foreign tenant denied only when enforcing, missing claim fails closed when enforcing,
allow-list keeps the workers through, user tokens untouched including the mapped-`sub` case).

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

## `DistClientDto` — secrets and roles over the bus (AB#5027)

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
engine also drops the CrateDB tables of the tenant's own archives when a tenant is dropped for good
(`DeleteChildTenantMetadataAsync(..., dropStreamData: true)` → `DropTenantDatabaseAsync`, best-effort),
so a Delete leaves no orphaned tables and the guard only has to ensure nothing is live. The deleting
settle sweep here re-drops only the database (its handle carries no archives — the tables were dropped
by the original delete).

## Tests

`tests/Infrastructure.Tests` — xUnit + FakeItEasy. `Infrastructure.csproj` exposes internals to this
assembly via `<InternalsVisibleTo Include="Infrastructure.Tests" />` so internal middleware such as
`TenantMiddleware` can be unit-tested directly (see `Middleware/TenantMiddlewareTests.cs`).
