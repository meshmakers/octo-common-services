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

**Every host of the middleware must call both halves.** As of AB#5047 all five do, and AB#5051 added
a sixth — the `Use…`/`Add…` pairs are:

| Service | `UseOctoTenantAuthorization()` | `AddOctoTenantAuthorization(builder.Configuration)` |
|---|---|---|
| octo-identity-services | `Program.cs` | `Program.cs` (AB#5032) |
| octo-communication-controller-services | `Program.cs` | `Program.cs` (AB#5032) |
| octo-asset-repo-services | `Configuration/OctoApplicationBuilderExtensions.cs` | `Program.cs` (AB#5047) |
| octo-bot-services | `Program.cs` | `Program.cs` (AB#5047) |
| octo-mcp-service | `Program.cs` | `Program.cs` (AB#5047) |
| octo-ai-services | `Program.cs` | `Program.cs` (AB#5051) |

They all pass `builder.Configuration` into the same helper, so the section name (a `const` here) and
the `OCTO_` env prefix every service registers cannot drift apart — a per-service section name would
be exactly the silent outlier this inventory exists to prevent. When adding a seventh host, add both
calls and a row here; `grep -rn "UseOctoTenantAuthorization" --include='*.cs'` across the checkout is
the audit.

**octo-platform-services deliberately has no gate (AB#5051).** It is the only service whose
`{tenantId}` route value is not the addressed tenant: `system/v1/tenants/{tenantId}/blueprints|ck-models`
are cross-tenant *operator* routes where the tenant id is the subject being inspected, and its only
other tenant route (`{tenantId}/_configuration`) is `[AllowAnonymous]`. Wiring the gate there would
403 the operator use case, because the **user**-token path below is unconditional. Instead AB#5051
switched that service from `ValidateAudience = false` to requiring `aud=octoAPI`, which is its
transport-level narrowing. Details in `octo-platform-services/CLAUDE.md`.

🔴 **The `AuthenticationType` blind spot — check this before trusting the gate in any host.** The
middleware skips every principal whose `Identity.AuthenticationType` is not `Bearer` (a guard against
false 403s on cookie principals). The JWT bearer handler labels its identity from
`TokenValidationParameters.AuthenticationType`, whose default is **`AuthenticationTypes.Federation`**,
not `Bearer` — verified against `Microsoft.IdentityModel.Tokens` 8.x. A host that does not set

```csharp
options.TokenValidationParameters.AuthenticationType = JwtBearerDefaults.AuthenticationScheme;
```

therefore runs the gate as a **silent no-op on every bearer request** — the user check *and* the
service-token audit log above, which is why several services' "inventory" was empty rather than clean.

🔴 **Setting the line is not enough — it has to survive the composition (AB#5054).** The options
factory runs every `IConfigureOptions<JwtBearerOptions>` in **registration order**. A host that
registers `ConfigureOptions<ConfigureJwtBearerOptions>()` and *then* calls
`AddJwtBearer(jwt => { jwt.TokenValidationParameters = new TokenValidationParameters { … }; })`
replaces the whole instance afterwards, discarding the label and the explicit `ValidIssuer`. Nothing
about that is visible: it compiles, and a unit test of the configurator class in isolation stays
green because it never sees the composed state. octo-ai-services shipped a full release in exactly
that condition (AB#5051 → AB#5056). **The rule is: one configurator owns the scheme, and
`AddJwtBearer()` is called without an argument.** Audit with
`grep -rn "AddJwtBearer(" --include='*.cs'` — every hit with an argument is a candidate.

Verified inventory (AB#5054), which is the thing to re-check rather than trust:

| Host | sets `AuthenticationType` | second configurator replacing `TokenValidationParameters` | user path effective |
|---|---|---|---|
| octo-mcp-service | ✅ AB#4315 | no | ✅ since AB#4315 |
| octo-ai-services | ✅ AB#5051 | had one → removed AB#5056 | ✅ since AB#5056 |
| octo-asset-repo-services | ✅ AB#5054 | no (delegate folded into the configurator) | ✅ since AB#5054, staged |
| octo-bot-services | ✅ AB#5054 | had one → removed AB#5054 | n/a — no `{tenantId}` route exists |
| octo-communication-controller-services | ✅ AB#5054 | had one → removed AB#5054 | ✅ since AB#5054, staged |
| **octo-identity-services** | ❌ **still missing** | no (its delegate only sets `Audience`) | ❌ **still a no-op** |

**octo-identity-services is the remaining gap.** It calls both halves and its API controllers use
`[Authorize(AuthenticationSchemes = "Bearer")]`, so the principal the middleware inspects really is
the JWT one — but nothing in `src/` ever sets `TokenValidationParameters.AuthenticationType`, so the
gate has never run there either. It was left out of AB#5054 deliberately (that work item scoped the
three services it names); arming it is the same two-step exercise and needs its own consumer
inventory, because identity is the one service every tenant-switch flow talks to.

`tests/Infrastructure.Tests/Configuration/TenantAuthorizationOptionsBindingTests.cs` pins the
properties a fleet-wide switch depends on: the unregistered default is `LogOnly` with an empty
allow-list for service tokens and **`Enforce` for user tokens**, and the section binds from both the
config section and `OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` /
`…__USERTOKENENFORCEMENT`.

## The user-token path is staged too (AB#5054)

Fixing the label flips the **user** path from "never checked" to "always refused" in one step, and
unlike the service path it had no staging at all. `TenantAuthorizationOptions.UserTokenEnforcement`
adds it, with the **opposite default**:

| Mode | Behaviour |
|---|---|
| `Enforce` (**default**, and the enum's zero value) | A user token may address only its own `tenant_id`. Everything else → **403**, including a user token with no `tenant_id` at all (fail closed). |
| `LogOnly` | Request outcomes identical to a host whose gate never ran, but every access an enforcing run would refuse is logged as a warning naming the **subject, the client id and both tenants**. |

The default is deliberately the strict one: `Enforce` means the option cannot weaken a host where the
check is genuinely live (mcp, ai-services), and a host that forgets to opt down arrives closed. There
is **no third "off" value** — `LogOnly` already changes no outcome, so a silent mode would only buy
the ability to hide the inventory. `allowed_tenants` is still never consulted on this path.

A service opts down in code, and **order matters**:

```csharp
builder.Services.AddOctoTenantAuthorization(o =>
    o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly);   // code default FIRST
builder.Services.AddOctoTenantAuthorization(builder.Configuration);      // configuration wins
```

Registered the other way round the code value would win and
`OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` would be inert — the same class of silent
outlier AB#5047 had to fix once already.

**Who is on `LogOnly` today, and why:** asset-repo and communication-controller (AB#5054). For
asset-repo the reason is concrete rather than precautionary — **meshmakers-app queries this service's
GraphQL endpoint cross-tenant with the user's own token**: `available-tenants.service.ts` walks the
root tenant and its children to discover the topology, and `tenant-provisioning.service.ts` probes
candidate tenants for the landing guard. The app's own code anticipates a 403 and degrades, but the
degradation is user-visible (a bare `/` visit lands on `/no-tenant?reason=unresolved` for every user
whose token tenant is not the root tenant), and that app runs in production. For the communication
controller no cross-tenant user caller was found — Studio re-mints per tenant and guards the route
client-side, octo-cli derives URL tenant and `acr_values` from one context value, and octo-mcp-service
RFC 8693-exchanges before calling — but that is an argument, not the log, so it takes one release in
`LogOnly` too. bot-services stays on `Enforce`: it has **no `{tenantId}` route segment at all** (job
tenants travel as query arguments / TUS metadata), so the middleware returns early and there is
nothing to stage.

Tests: `tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareTests.cs` (foreign tenant
and missing claim denied by default, both logged-and-allowed when staging, matching tenant never
logged, and the two staged paths independent — a `LogOnly` user path must not re-open the AB#5032
service-token exemption).

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

## The parent-tenant administration rule — opt-in per endpoint (AB#5060)

A parent administrator administers their child tenants: backup, restore, archive export, fixups.
Those operations move onto tenant routes, where the exact `tenant_id` match would 403 a parent's
token against a child's route. The gate therefore knows a second way for a **user** token to pass:
the route tenant is a **child** of the token tenant.

🔴 **It is opt-in per endpoint, and that is the whole design.** The right to *administer* a child
tenant is not the right to *read its data*. A blanket relaxation would hand the parent every data
route of the child — GraphQL, entities, queries, everything — which is precisely what Gerald ruled
out. So the rule fires only where the operation itself says it is tenant administration:

```csharp
[AllowParentTenantAdministration]           // src/Infrastructure/Authorization/
[HttpPost("{tenantId}/v1/tenants/backup")]  // ← marks THIS operation, nothing else
```

The middleware reads it off the endpoint metadata (`GetMetadata<IAllowParentTenantAdministration>()`),
exactly like it already honours `[AllowAnonymous]` — so it works for controllers (attribute on action
or controller) and for minimal APIs (`.WithMetadata(new AllowParentTenantAdministrationAttribute())`),
and the decision is visible where the operation is instead of in a path list that drifts. **An
unmarked endpoint behaves exactly as before**, which is why shipping this widens nothing: as of
AB#5060 **no endpoint in the estate carries the marker**.

| Should carry it later | Must never carry it |
|---|---|
| Tenant backup / restore / clone (asset-repo `TenantController`, when those move from `system/…` to `{tenantId}/…`) | Any GraphQL endpoint (`/{tenantId}/graphql`) |
| Archive data export / import (AB#4230, AB#4231) | Entity / query / stream-data read and write routes |
| Operator fixup and reconcile routes that act *on* a tenant | `…/enable` / `…/disable` and other capability toggles — they change what the child *is*, and the child's own admin owns that |
| Tenant delete / detach — only if the product ever wants a parent to do it | Identity routes of the child (users, roles, clients): membership is the child's own directory |

Mechanics, and why each was chosen:

- **Service tokens are excluded — this is the reason the rule is safe at all.** A client-credentials
  token's `tenant_id` proves nothing: mirrored clients (`AutoProvisionInChildTenants`) share the
  parent's secret, so whoever holds a child's credentials holds the parent's, and a token minted
  without `acr_values` falls back to the **system tenant** (AB#5058, AB#5061, AB#5065) — which is the
  **root** of the hierarchy. An ancestor rule on that path would therefore give every service client
  of the authority every tenant route in the estate: a large amplification of a known weakness. A
  *user* token's `tenant_id`, by contrast, means a real authentication against that tenant's user
  directory. Service clients that genuinely fan out keep using `CrossTenantServiceClientIds`.
- **Reach: the parent's own registry, one level.** The hierarchy is navigable **downwards only** —
  each tenant's database holds one `RtTenant` record per *direct* child, and there is no published
  API returning a tenant's parent. A subtree walk is therefore a descending BFS whose *width* is
  unbounded and whose every intermediate node must be materialised as a tenant context (CK
  auto-imports, connection pool); capping the depth does not cap that, and capping the width would
  make an authorization answer depend on registry enumeration order. The deep case that actually
  occurs is covered exactly anyway: for the **system** tenant the registry consulted *is* the
  platform-wide one, so a platform operator authenticated in the system tenant reaches every tenant —
  correct, the system tenant being the root. If a *mid-level* parent ever needs its grandchildren,
  the honest fix is an upward walk over the system registry's `ParentTenantId` (O(depth), no
  fan-out), which needs an engine API that does not exist today — not a fan-out dressed up as a
  depth cap.
- **Equality costs nothing.** The exact match is answered before the metadata lookup and before any
  resolution, and the hierarchy is only consulted on a marked endpoint with a *different* tenant.
  Pinned by a call counter in the tests, not by reading the code.
- **One resolution per tenant pair per minute.** `ITenantHierarchyReader` /
  `TenantHierarchyReader` (`src/Infrastructure/Services/`, singleton, registered by
  `AddOctoServiceInfrastructure`) reads through `ISystemContext` — already a per-request dependency of
  `TenantMiddleware`, so no new dependency — using `IsChildTenantExistingAsync`, deliberately **not**
  `TryGetChildTenantContextAsync`, whose resolve runs the CK auto-imports (same reasoning as
  `IsTenantRegisteredAsync`, AB#4829). Answers are cached for
  `TenantAuthorizationOptions.TenantHierarchyCacheDuration` (default **60 s**, `TimeSpan.Zero`
  disables): a new child is unreachable for at most one TTL, a deleted one reachable for at most one
  TTL. **Negative answers are cached too** — the denial path is the attacker-controllable one. The
  cache is capped at 1024 pairs because the route tenant comes from the URL.
- **Fail closed everywhere.** An unreadable hierarchy, an unknown parent, or a host that marked an
  endpoint without registering the reader all answer "not related" (the last one with a warning), so
  the request falls back to the exact match.
- **No global on/off switch, on purpose.** Its scope is the set of marked endpoints, decided in code.
  A flag could only either break exactly those endpoints or — the dangerous direction — be widened
  into the blanket rule this deliberately is not.
- **Every grant is logged at `Information`**, naming subject, client id, both tenants and the
  endpoint. The rule widens access and denies nothing new, so a `LogOnly` stage would observe
  nothing; the grant log is the equivalent record of who actually uses it. It is evaluated **before**
  the `UserTokenEnforcement` branch so that a granted access never appears in the AB#5054 `LogOnly`
  inventory as "would be denied" — it would not be.

Tests: `tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareTests.cs` (granted on a
marked test endpoint, denied on the identical unmarked one *without* asking the hierarchy, unrelated
tenant denied, service token never allowed by the rule, equality never resolves, missing reader fails
closed) and `tests/Infrastructure.Tests/Services/TenantHierarchyReaderTests.cs` (registry probe not
context materialisation, positive and negative caching, TTL of zero, self is not a child, unreadable
hierarchy fails closed).

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
