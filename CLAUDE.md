# CLAUDE.md — octo-common-services

Shared .NET infrastructure libraries used across all OctoMesh backend services. See `README.md`
for the published package list. Targets .NET 10, DebugL for local development.

## Build / Test

```bash
dotnet build Octo.Common.Services.sln -c DebugL
dotnet test  Octo.Common.Services.sln -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Versions come only from the pipeline (AB#6291): `Directory.Build.props` has no release fallback, so a
Debug/Release build without `-p:OctoVersion=X.Y.Z` (or `-p:OctoNugetPrivateServer=<feed>` for the main
line `0.1.*`) fails fast with MSBuild error `OCTO0001`. Use `-c DebugL` locally. Every project in
`Octo.Common.Services.sln` must map the solution configuration `DebugL` to the project configuration
`DebugL`: `TestAssemblyCorrectMigrations` was mapped to `Debug` and so restored `3.4.*` packages from
nuget.org inside a DebugL build, which only surfaced once the fallback was gone.

Propagate the produced `Meshmakers.Octo.Services.*.999.0.0.nupkg` files from `bin/DebugL/` into
`../nuget` so downstream services (Asset-Repo, Reporting, Identity, ...) pick up local changes.

## `/metrics` is internal-only (AB#5868)

`MapObservability()` serves the Prometheus scrape endpoint `/metrics` only to connections that arrive on
the port named by `Observability:PrometheusPort` (env `Observability__PrometheusPort`). Unset or `0` (the
default) means the endpoint does not exist (404) — metrics reach Dash0 through the OTLP exporter, nothing
scrapes `/metrics` today. The guard is the socket's local port, never the `Host` header (caller-controlled).

The port must be a Kestrel listener that the Service and the Ingress never route to (chart: container port
only, e.g. `ASPNETCORE_URLS=http://+:80;http://+:9464`). The endpoint used to sit on the main port and was
reachable unauthenticated through every public ingress, with tenant ids, pipeline names and pool ids in
the output. Do not map `/metrics` (or `MapPrometheusScrapingEndpoint`) on the main listener again;
`PrometheusEndpointTests` fails if you do.

## `/health` needs a Bearer token (AB#6739)

`/health` writes the full health report (check names, status, descriptions, CPU/memory figures), so
`MapObservability()` lets only callers through that authenticate with the `Bearer` (JWT) scheme; anonymous
callers get `401` with `WWW-Authenticate: Bearer` and no body. Any valid token is enough, no role. The
check runs inside the endpoint (`WebApplicationExtensions.IsAuthenticatedAsync`), not through
`RequireAuthorization()`: it must not depend on `UseAuthorization()` (the mesh adapter never calls it) and
it fails closed (401) on hosts that register no `Bearer` scheme (EDA and FinAPI adapters). The browser
consumers (Studio / MeshBoard `HealthService`, AB#6739) send the user's token. `/healthz/live` and
`/healthz/ready` stay anonymous endpoints that return a plain-text status body for the kubelet probes. `HealthEndpointTests` fails
if `/health` becomes anonymous again.

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

## The `{tenantId:tenantId}` route constraint (AB#5060)

`TenantIdRouteConstraint` (`src/Infrastructure/Routing/`) decides whether a path segment **can be** a
tenant id. Register it with `builder.Services.AddOctoTenantIdRouteConstraint()` — every host that
serves a tenant-addressed route must, or ASP.NET throws at startup on the unknown constraint key.

🔴 **It is a routing constraint, not an authorization check.** Whether the caller may address that
tenant is `TenantAuthorizationMiddleware`'s job; whether the tenant exists is the resolving service's.
Nothing that needs a lookup belongs here — the constraint runs during route matching, before
authentication, on every request.

It accepts exactly what tenant **creation** accepts (`TenantContext.ValidateTenantIdFormat`: at most
64 ASCII letters, digits, `-` or `_`), so it cannot reject a tenant that could exist. Keeping the two
in step is the point: a laxer constraint lets a segment that can never name a tenant travel on into
services that use it as a Mongo database name, a cache key or a directory name; a stricter one 404s a
real tenant. The length bound is duplicated rather than referenced — this assembly deliberately does
not depend on the runtime engine — and `TenantIdRouteConstraintTests` pins the shared value.

⚠️ **Consolidated from seven copies.** Every tenant-serving host used to carry its own `internal`
class plus its own `ConstraintMap.Add` line, and they had drifted: five accepted any non-null value
(whitespace and dot segments included), two carried an empty `if (isMatch) { }` block, asset-repo
wrote an untyped `HttpContext.Items["d"]` that nothing read, and only platform-services rejected an
empty string. Nothing made the divergence visible, which is the reason it is one implementation now.
Do not add a local copy back.

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
| `LogOnly` | Request outcomes identical to before, but every access an enforcing run would refuse is logged as a warning naming the **client id, the token tenant and the route tenant**. This log is the consumer inventory. Was the default until AB#5077. |
| `Enforce` (**default since AB#5077**) | A service token may address only its own `tenant_id`, or a tenant it is allow-listed for. Everything else → **403**, including a token with no `tenant_id` at all (fail closed). |

Bind it with `services.AddOctoTenantAuthorization(configuration)` (section `TenantAuthorization`, i.e.
`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` / `…__CROSSTENANTSERVICECLIENTIDS__0`). The call is
optional in the compiler's sense only — without it the defaults apply, so a consumer that never wires
it keeps today's behaviour, but its enforcement mode is then **not settable at all**: the environment
variable is inert. Since AB#5077 that cuts the other way: an unwired host arrives **closed** (both
paths enforce), but its environment can no longer opt down either, because the variable only takes
effect through the binding.

🔴 **AB#5077 flipped the service-token default to `Enforce`.** The staging had done its job: identity
stamps the claim (AB#5032), the guessable no-`acr_values` fall-back is refused for mirrored clients
(AB#5058), and the caller inventory found every production caller passing `acr_values`. What can still
break does so **only at run time**: a client-credentials login that omits `acr_values` gets
`tenant_id` = the system tenant, and the ancestor rule never widens a service token, so it is refused
on every child-tenant route. An environment that still needs its inventory sets
`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT=LogOnly`.

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
properties a fleet-wide switch depends on: the unregistered default is `Enforce` with an empty
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

## Denials name their reason (AB#5227)

Every 403 this middleware produces — user path and service path — now carries a JSON body
`{"message": "..."}` naming the reason and the remedy, and the user-path `Enforce` denial is logged
as a **Warning** (it used to set the bare status code silently, so an operator saw a generic
"access denied" toast and found nothing in the logs). The body shape matches what
`MmHttpErrorInterceptor` (octo-frontend-libraries, shared-services) renders for a 403 with a
message, so the reason reaches the operator's toast.

The motivating incident: the Studio's "Restore Tenant" flow uploaded a backup and called the
tenant-routed `restore-from-upload` on bot-services for a tenant that had just been **deleted**.
The parent-tenant rule above only grants access to an *existing* child, so the request failed on
this exact branch with nothing actionable anywhere. The marked-endpoint denial message therefore
covers both causes in one sentence ("not an existing child tenant of it — if the tenant was
deleted, re-create it first") without telling an unrelated caller which of the two applies, and
mentions that a tenant created moments ago may take up to one TTL of the hierarchy cache (60 s) to
be recognized. The body is written best-effort (`Response.HasStarted` guard).

## Tenant role policies: scope plus role in one place (AB#7319)

`services.AddOctoTenantRoleAuthorization()` and `options.AddTenantRolePolicy(policyName, roleName)`
(`src/Infrastructure/Authorization/TenantRoleAuthorizationExtensions.cs`) define "scope `octo_api` full access
**plus** a role of the tenant in the token". Lifted from the asset repository's `TenantAdministrationPolicy`
(AB#4497); use it for every operation that administers the tenant itself (capability enable/disable, tenant
administration) instead of a local `RequireAssertion`. The role is probed as raw `role` claim and as the
inbound-mapped `ClaimTypes.Role` (`TenantRoleAuthorizationHandler.HasTenantRole`).

- Scope and role are ONE requirement (`TenantRoleRequirement`), checked scope first. A refusal is a `403` with the JSON body
  `{"message": "..."}` (AB#5227 shape) naming whichever is missing (the scope `octo_api`, else the role):
  `ForbiddenReasonAuthorizationResultHandler` replaces `IAuthorizationMiddlewareResultHandler` and writes the reason
  of a failed `TenantRoleRequirement`; every other outcome stays with the framework default. A host that already
  registers its own result handler must chain it.
- Fail closed: a host that adds the policy but not `AddOctoTenantRoleAuthorization()` has no handler for the
  requirement and refuses every request.
- Tests: `tests/Infrastructure.Tests/Authorization/TenantRoleAuthorizationTests.cs`.

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

**In-memory retry that needs no MongoDB (AB#7249).** The durable store above is optional (only Identity and
Asset-Repo wired it) and best-effort: during a MongoDB primary election its own write times out and is swallowed.
Staging-1, 2026-10-21: the communication controller logged "this tenant will be retried in the background" for
`sbeg` and `eegroider`, but wired no store, so nothing was ever retried and their adapters stayed offline until a
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
- **The retry loop runs during the startup pass (AB#7271).** `FailedTenantRetryLoop` is not a hosted service any more:
  hosted services start sequentially and `HostedInitializer.StartAsync` blocks for the whole pass (all tenants), so a
  loop registered behind it only started once the slowest tenant was done (test-2, 2026-10-22: announced "+30 s",
  actual ~5 min). `HostedInitializer` now starts the loop (if `AddOctoServiceInfrastructure` registered it) **before**
  the initialization services run and stops it in `StopAsync`. The first tick is 30 s after the start, then every 30 s
  (`PeriodicTimer` over `TimeProvider`), so a due retry waits at most for the next tick (30 s after the `not before` time in the
  log line, as long as the previous tick has finished: a tick retries tenants one after the other, so a slow tenant delays
  the rest — unchanged). `StopAsync` waits for a running tick only until the host's shutdown deadline. Consumers need no change. Test: `FailedTenantRetryLoopTests` (fake time, retry due before the pass ends
  runs while the pass is still running).
- **System tenant: fail-fast stays (AB#7271 decision).** If `SetupAsync` of the system tenant (`octosystem`) fails,
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
- **`PosUpdateTenant` scope guard (AB#5497).** After the echo guard, the consumer checks
  `PosUpdateTenant.Scope`: a `TenantUpdateScope.CacheOnly` update (AB#4895 — the nightly
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

## System.Notification CK model: ownership markers are enforced (AB#6326)

`src/Notifications/Notifications.csproj` sets `OctoEnforceRuntimeStateMarkers=true`: the `CkLintRuntimeStateMarkers` task (CK
MsBuildTasks package) fails the build with `OCTO-CK001` when an attribute in `ConstructionKit/attributes/*.yaml` declares no `ownership`
(or the deprecated `isRuntimeState`). AB#6326 restated the existing behaviour: 8 attributes got `ownership: SeedOwned`; the five
`TenantOwned` attributes of AB#5359 (mail switch, redirect URL, template subject/body/rendering) are unchanged. Same resolved ownership,
so `octo-ckc ValidateVersion` reported "no bump required" and System.Notification stayed 2.4.0; AB#6691 re-pinned it to 2.5.0 after the range-retention flip, and AB#2585 raised it to 2.6.0.
**Review question for every new attribute:** could an operator or user type this value in the product? Yes → `TenantOwned` (or `Secret`
for a credential); a service writes it → `RuntimeState`; the product ships and corrects it → `SeedOwned`.

## System.Notification 2.6.0: notification subscriptions (AB#2585)

Three types and four associations (`types/notificationSubscription.yaml`, `notificationPreference.yaml`,
`notificationDeliveryState.yaml`, `associations/subscription-associations.yaml`, attributes in `attributes/subscription-attributes.yaml`):

| Type | Holds | Written by |
|---|---|---|
| `NotificationSubscription` | what to send: `SubscriptionQuery` (a SimpleRtQuery), `SubscriptionTemplate`, `Enabled` (= Deployed), `DeployedAt` | Studio; `Enabled` / `DeployedAt` only by the communication controller's deploy / undeploy |
| `NotificationPreference` | one recipient: `SubjectId` (token `sub`), `CronExpression`, `TimeZone`, `SkipExistingRows` (Onwards / Catch up), `PreferenceSubscription` | Studio |
| `NotificationDeliveryState` | the engine's bookkeeping for one preference (`DeliveryStatePreference`): `ProcessedUntil` (the latest change handled), `SentRows` (record `SentRow`: `RowRtId` / `SentAt` / `ChangedAt`, kept 30 days and at most 10 000, the adapter prunes it), `QueryRtId`, `DeliveredToSubjectId` / `DeliveredWithSkipExistingRows` (property names `SubjectId` / `SkipExistingRows`: which recipient and start option the rows belong to), `LastRunAt`, `LastSentAt`, `LastError`, `RunningSince` | mesh adapter (`PrepareNotification@1` / `CommitNotificationState@1`); the communication controller only erases a state whose preference is gone |

- **Deliberate exceptions to the review question above.** `DeployedAt` and the delivery state's `ProcessedUntil`,
  `SentRows`, `QueryRtId`, the two `Delivered*` attributes, `LastRunAt` and `LastSentAt` are written by services but are `TenantOwned`, not `RuntimeState`:
  `RuntimeState` is left out of an `ExportRt`, and a tenant moved without them would mail every recipient rows they already got
  (and treat every Onwards start as new). Only the lease (`RunningSince`) and `LastError` are instance-local `RuntimeState`.
- `QueueNames.NotificationScheduleChannelName` is the prefix of the engine's tick queue
  (`{prefix}-{tenantId}-{pipelineRtId}`, lower-cased); the controller sends to it and the adapter's `FromNotificationSchedule@1`
  listens on it, so it is a cross-repo contract (pinned in the adapter by `FromNotificationScheduleNodeTests`). The message itself,
  `NotificationScheduleTick`, is pinned by `NotificationScheduleTickWireContractTests`.

## CK method wire contract (CK v2 Phase 3, AB#6874)

Namespace `Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods` (`src/Contracts/DistributionEventHub/Methods/`),
shared by the dispatcher (asset-repo), the handler SDK (`Meshmakers.Octo.Services.Methods`, AB#6875) and handlers
(identity, communication controller, ...). Full reference with field tables, platform error codes and the rules of
evolution: `docs/method-wire-contract.md`.

- `CkMethodRequest` (sealed, redacting `ToString()`; `ParametersJson` and `CallerAccessToken` are **sensitive**),
  `CkMethodResponse` (response envelope with `Success`, `Error`, `ResultJson`, `Operation`), `CkMethodError` (stable `Code` +
  string `Parameters`, no message text on the wire), `CkMethodOperationHandle` (async handle, F3.6),
  `CkMethodHandlerAnnounced` / `Withdrawn` / `DiscoveryRequested`, `CkMethodQueueNames`, `CkMethodErrorCodes`
  (constants + `Catalog`, single source; `METHOD_` is reserved for the platform), `CkMethodErrorParameters`.
- **Parameter size limit** (AB#7665, F-2): `METHOD_PARAMETERS_TOO_LARGE` (parameters `methodId`, `maxBytes`, `actualBytes`); limit = UTF-8 byte length of `ParametersJson`, option `Methods:MaxParametersBytes` (`CkMethodLimitsOptions`, default 1 MiB, one name for gateway and host, bound by `AddCkMethodDispatcher`, `AddCkMethodHandlers`, `AddCkInProcessMethodHandlers`). `CkMethodParameterLimit.Exceeds/TooLarge` is the one check: the gateway (asset-repo) calls it before validation, `CkMethodHandlerInvoker` before the token is validated and before deserialization. Tests: `CkMethodHandlerInvokerTests`, adversarial C12.
- **Idempotency store contract** (AB#7817, F3.5, decided in OD-4 AB#6922; `src/Methods/Idempotency/`, tests `tests/Methods.Tests/Idempotency/IdempotencyStoreTests.cs`): `ICkMethodIdempotencyStore`
  (`ClaimAsync` atomic, expired record taken over by a conditional replace; `CompleteAsync(Completed|Unknown)`; `ReleaseAsync`; owner-only, only while `InProgress`; fails closed by
  throwing `CkMethodIdempotencyStoreException`), `CkMethodIdempotencyKey` (tenant + method + caller subject from the principal provider + key, stored `Id` = SHA-256 hex, raw key not at rest, max 128 chars),
  `CkMethodIdempotencyRecord` (reference equality for the conditional replace; `ToString` prints no fingerprint or response), `CkMethodInMemoryIdempotencyStore` (tests and single-replica hosts; the Mongo store is AB#7824 in
  asset-repo, the band-300 filter is K4). Row 4: only not-executed codes `ReleaseAsync`; an unknown outcome and `METHOD_HANDLER_FAILED` burn the key. Option `Methods:Idempotency:Ttl` (`CkMethodIdempotencyOptions`,
  `CkMethodIdempotencyLifetime`): default 24 h, max 7 d (clamped + one warning), zero/negative fails start. New platform code `METHOD_REQUEST_IN_PROGRESS` (`NotExecuted`, parameter `methodId`; additive, wire contract version unchanged).
  Registration `AddCkMethodIdempotencyOptions()` / `AddCkMethodInMemoryIdempotencyStore()`. The fingerprint keys are `CkMethodFingerprintOptions` (AB#7819, see the invocation pipeline).
- **Deadline clamp** (AB#7665, L-4): the invoker clamps `DeadlineUtc` to now + `Methods:Dispatcher:MaxTimeout` (`CkMethodDeadline.Clamp/MaxOf`, the dispatcher's helper) and logs the clamp once at Information.
- **Versioning**: `CkMethodContract.CurrentVersion` (int, major) on request, response and announcement; additive changes never
  bump it, unknown members are ignored, a missing version reads as 1; a newer message is answered with
  `METHOD_CONTRACT_VERSION_UNSUPPORTED`.
- **Tests** (`tests/Contracts.Tests/Methods`): golden JSON files (`Golden/*.json`) pin the serialized form under the hub
  serializer options; unknown members, missing version, redaction, error-code catalog and the contract document are
  tested. After an additive change refresh the goldens with `OCTO_UPDATE_GOLDEN=1 dotnet test tests/Contracts.Tests` and
  review the diff. Adding a platform code needs a constant, a `Catalog` entry and a row in `docs/method-wire-contract.md`.

## CK method handler SDK (CK v2 Phase 3, AB#6875)

Package `Meshmakers.Octo.Services.Methods` (`src/Methods/`, tests in `tests/Methods.Tests` and `tests/Methods.IntegrationTests`,
the latter against a private RabbitMQ container). It is the **service side** of a CK method: a typed handler on top of the
wire contract above, for the remote (bus) transport, plus the in-process transport (AB#6876, section below). The dispatcher
and registry (F3.2/F3.3) and the pipeline handler (AB#6879) are other stories. Developer guide with a working sample:
`docs/method-handler-sdk.md`.

- **Typed handlers**: `ICkMethodHandler<TParameters, TResult>` (method with a result) or `ICkMethodHandler<TParameters>`
  (no result), `HandleAsync(CkMethodContext<TParameters>, CancellationToken)`. `TParameters`/`TResult` are the generated
  `{Type}{Method}Parameters` / `{Type}{Method}Result` records. A handler is resolved from a **new DI scope per invocation**,
  so it can take scoped services. `CkMethodContext` has caller, tenant, target, correlation, deadline/`RemainingTime`,
  access token; **no database session and no transaction** (methods run outside the request transaction). `CkMethodContextSessionFreeTests` (reflection over the context, handler
  abstraction, outcome and wire records) fails if any reachable member is a session or transaction type or a `MongoDB.*` type;
  `MethodFailureIsolationTests` pins rule 2 (a failing method call leaves earlier committed writes untouched, and a failing
  earlier write does not block an independent call).
- **Outcome and errors**: return the result (implicit conversion), `CkMethodOutcome.Ok()`/`Fail(code, params)` or the platform
  helpers (`ValidationFailed`, `TargetNotFound`, `Forbidden`), or throw `CkMethodException`. `CkMethodFailure` validates the
  code (declared shape or platform code). With CK validation on, a code the method does not declare becomes
  `METHOD_HANDLER_FAILED` (handler bug). Any other exception becomes `METHOD_HANDLER_FAILED` with a `correlationId`
  parameter (request correlation id, else the invocation id); the exception is logged with that id, never sent.
- **Host core** `ICkMethodHandlerInvoker` (public, transport independent): contract version check
  (`METHOD_CONTRACT_VERSION_UNSUPPORTED`), unknown method (`METHOD_HANDLER_NOT_AVAILABLE`, reason `method-not-registered`),
  deadline already passed (`METHOD_TIMEOUT`, not executed), deadline cancellation of the handler token (`METHOD_TIMEOUT`; `timeoutSeconds` =
  `request.TimeoutSeconds`, the definition's value only as fallback),
  parameter deserialization with `CkMethodJson.Options` (invalid or missing member -> `METHOD_VALIDATION_FAILED` with the
  member name only, never the value), result serialization. It never throws for a request; only the caller's own
  `CancellationToken` surfaces as `OperationCanceledException`. Other transports call this instead of re-implementing it.
- **Bus transport** (`CkMethodBusConsumer`, internal): one shared command endpoint per method (`CkMethodQueueNames.For`, registered
  with `RegisterCommandConsumer(name, handler, CommandConsumerOptions { SuppressFaults = true })`; senders use
  `GetCommandResponseAsync(name, request, CommandSendOptions { Mandatory = true, ... })`, a missing consumer fails with
  `CommandNotRoutableException`). A possible duplicate delivery (`CommandDeliveryInfo.IsPossibleDuplicate`: broker redelivery or
  retry) is not executed and answered `METHOD_OUTCOME_UNKNOWN` (all methods count as non-idempotent; the log line names invocation id,
  tenant and delivery info, never parameters); a repeated invocation id is the second line, with the same answer. The decision is the
  public `CkMethodSeenInvocations.Decide(request, delivery)` (`Fresh | PossibleDuplicate | AlreadySeen`, AB#7818): every host of method
  handlers uses it instead of its own dictionary; the bus consumer only logs and answers. The seen-ids
  retention is `min(DeadlineUtc, now + 1 h) + 5 min`, capped at `now + 1 h` (an endless deadline from another sender cannot overflow). A failing response send is swallowed (a rethrow would let the bus retry run the method
  again, AB#4348).
- **Life cycle** (all internal, `AddCkMethodHandlers(serviceName, m => m.Handle<THandler>(methodId))`): start order 15 validation,
  20 event hub, 25 endpoints, wait for `WaitUntilReadyAsync` of every endpoint (`Methods:EndpointReadyTimeout`, 30 s; timeout = start
  failure) and only then the announcement, heartbeat hosted service (15 s, TTL 45 s from `CkMethodHandlerOptions`, **final** values, AB#7838; validated at startup by
  `CkMethodHandlerOptionsValidator`: interval >= 1 s, TTL >= 2 x interval and <= 24 h, a violation stops the host; keys `Methods:HeartbeatInterval` /
  `Methods:Ttl`, env `OCTO_Methods__HeartbeatInterval` / `OCTO_Methods__Ttl`), withdraw before
  the endpoints are removed on graceful stop, `AddCkMethodHandlerDiscovery()` in the `AddDistributionEventHub` configuration
  answers dispatcher discovery with an immediate announcement.
- **Startup validation** (`Methods:ValidateRegistrations`, default on): every registration is checked against the CK cache of the
  system tenant (or `Methods:ValidationTenantIds`): unknown method id, a CK parameter without a property on `TParameters`,
  scalar type mismatch, a required property without a CK parameter, result/no-result mismatch. A failure stops the service
  and names the method. The check also records sensitive parameter names and declared error codes for run time.
- **Sensitive data**: the host never logs `ParametersJson` or the token (`CkMethodRequest.ToString()` redacts, the host logs ids
  only). `CkMethodRedactor` additionally masks the values of CK `sensitive: true` parameters and the caller token in the
  message, stack trace and `ToString()` of an exception a handler throws before the host logs it. Tests: `CkMethodHandlerInvokerTests`.
  **Sensitive result (AB#7007)**: `CkMethodResponse.ToString()` never prints `ResultJson` (ids, success, error code, operation id and
  `hasResult` only); the host never puts the result into a log line, an exception message, an activity tag or a metric label. A
  failure while the host *serializes* the result (`CkMethodResultSerializationException`, thrown by the adapter) is logged with its
  inner exception only for a non-sensitive result; for a CK result marked `sensitive` (`CkMethodRegistration.SensitiveResult`) only the
  fault type is logged. A failing `respond` of the bus consumer is logged through `CkMethodRedactor.WithResult(resultJson)` (masks every
  value of the result, plain and escaped) when the result is sensitive or the method is not registered. Tests: `RedactedResponseViewTests`.
- **Event hub requirement (AB#6891)**: the endpoint readiness, delivery info, fault suppression and mandatory send come from
  `Meshmakers.Octo.Common.DistributionEventHub` (opt-in primitives, see its `CLAUDE.md` "Shared Command Endpoints"). This repo
  needs the event hub release that contains them. The migration of the older exclusive command queues is a separate story.

### In-process transport and invocation pipeline (AB#6876)

For methods the calling service owns itself (`Tenant.*`, `Archive.*` of the asset-repo gateway). Files: `src/Methods/InProcess/`,
tests `tests/Methods.Tests/InProcess/`. Guide: `docs/method-handler-sdk.md` section "In-process handlers".

- **Registration**: `AddCkInProcessMethodHandlers(m => m.Handle<THandler>(methodId))`, the same typed handler classes as for
  `AddCkMethodHandlers`. In-process registrations live in their own `CkInProcessMethodRegistrations`: no bus queue, no
  announcement, no heartbeat, no event hub needed. A method registered in both places stops the startup
  (`CkInProcessMethodValidationService`, order 15, also validates against the CK cache).
- **Transport** `ICkInProcessMethodTransport` (public): `CanHandle(methodId)` (a dispatcher asks this before its registry lookup),
  `MethodIds`, `InvokeAsync(CkMethodRequest, ct)`. It wraps the same internal `CkMethodHandlerInvoker` as the bus consumer
  (own instance with the in-process registrations), so the envelope is identical (tests compare serialized envelopes for success,
  declared error, exception, deadline, unknown method, contract version, validation). The handler runs in a new scope of the
  root provider (not the caller's HTTP scope) and outside the caller's transaction. A handler that ignores its token is not
  waited for past `DeadlineUtc`: `METHOD_TIMEOUT` (`CkMethodHandlerInvoker.TimeoutFor`).
- **Detached dispatch and tripwire** (AB#7022, OD-8 part 1; `CkDetachedExecution.cs`, `CkInProcessAmbientStateProbe.cs`; tests
  `InProcessIsolationTests`, `CkMethodDispatcherBusTests` in-process cases): `RunAsync` starts the invoker through
  `CkDetachedExecution.Run` = `Task.Run` under `ExecutionContext.SuppressFlow()`, so the handler's flow has an **empty** ambient context
  (an `AsyncLocal` set by the caller, e.g. the asset-repo request session, is not visible, also not after the first `await`; nothing set by the
  handler leaks back). Carried explicitly, nothing else: the trace (`Activity.Current` of the caller is restored in the flow; our
  `ActivitySource` `CkMethodTelemetry.ActivitySourceName` = `Meshmakers.Octo.Methods` starts the handler span `CK method handle` as its child,
  tags `ck.method.id`, `ck.method.transport`; subscribed in `ObservabilityBuilder`; without a listener the caller's trace still continues),
  `CurrentCulture`/`CurrentUICulture`, the cancellation token with the deadline (argument), and the call-identifying logging scope
  (`MethodId`, `InvocationId`, `CorrelationId`, `TenantId`, opened by the invoker inside the detached flow). The caller's logging scopes,
  principal scope (`CkMethodPrincipalScope`), HTTP context and any other `AsyncLocal` are deliberately dropped. **Tripwire**: a host registers
  `AddCkInProcessAmbientStateProbe<TProbe>()` (`ICkInProcessAmbientStateProbe.IsRequestStateVisible(IServiceProvider handlerScope)`, singleton,
  thread-safe, never throws, yes/no only). `CkMethodHandlerInvoker` asks every probe (internal `ICkMethodEntryGuard`, in-process only; the bus
  consumer passes none) after the new scope exists and before the handler is resolved; a probe that reports state or throws (fail closed)
  fails the call with `METHOD_HANDLER_FAILED` (parameters `methodId`, `correlationId`; no new wire code) and logs ERROR naming the probe
  type and the exception type only. No probe registered = no check. There is deliberately no switch to turn the detach off. Filters of the
  pipeline (audit, authorization) still run in the caller's context, before the detach.
- **Pipeline** `ICkMethodInvocationPipeline` / `ICkMethodInvocationFilter` (public, `AddCkMethodInvocationFilter<T>()`): ordered
  steps around any transport (`CkMethodTransportNames.InProcess` / `Bus`); lower `Order` runs first, bands in
  `CkMethodInvocationFilterOrder` (Audit 100 outermost, Authorization 200, Idempotency 300 innermost; decided by Odin: exactly one audit event per invocation incl. refusals and replays). A filter may refuse (return a failed response), pass a
  modified request to `next`, or wrap the call. An exception in a filter becomes `METHOD_HANDLER_FAILED`; caller cancellation
  passes through. The F3.3 dispatcher runs its bus send as the terminal delegate of the same pipeline instance.
- **Method authorization filter** (AB#6911, F3.4; `src/Methods/Authorization/`, tests `tests/Methods.Tests/Authorization`,
  `tests/Methods.IntegrationTests/CkMethodAuthorizationBusTests.cs`): `AddCkMethodAuthorization()` registers
  `CkMethodAuthorizationFilter` at band 200 through `AddCkMethodInvocationFilter<T>()` (once per service) plus the shared
  `ICkMethodAuthorizationEvaluator` (public, pure: `Evaluate(authorization, kind, targetRtId, principal)`; the gateway gate AB#6912 uses
  the same instance), `ICkMethodPrincipalProvider` (default `CkMethodPrincipalScope`, an `AsyncLocal` the gateway or job opens with
  `CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(user))` around the call) and `ICkMethodAuthorizationDefinitionSource`
  (CK cache: concrete target type `AllMethods`, then the declaring type). **The principal is never read from `CkMethodRequest.Caller` or the
  forwarded token**; no principal = refused. Rules: `octo_api` always required, then all declared `scopes` (administrators too); `roles` any-of,
  case-insensitive; `allowSelf` compares parsed `OctoObjectId`s (instance methods with a target only; honoured also with empty roles);
  **default-deny**: no `authorization` or empty `roles` admits only `Methods:Authorization:AdministratorRoles` (default `AdminPanelManagement`)
  and, if declared, the target itself; an administrator is not implicitly admitted to a method that lists roles. An unknown method definition
  is refused. A refusal is a normal failed response `METHOD_FORBIDDEN` with `methodId` and the fixed reason `not-authorized`
  (`CkMethodAuthorizationErrors.Forbidden`): the filter never loads the target, so the answer is the same whether it exists or not; the log
  names the rule (`CkMethodDenial`) but no token, parameter or target id. Audit (100) sees the refusal, idempotency (300) and the transport
  never run. Token trust for remote handlers is settled in the next entry (AB#6913). Open decisions that this filter does not settle:
  CK data permissions for method calls (AB#6917, D3:A), OD-9 (AB#6918).
- **Caller token validation for remote handlers** (AB#6913, F3.4b; decision AB#6916 option A; `src/Methods/Authorization/Token/`, tests
  `tests/Methods.Tests/Authorization/Token`): the bus is not authenticated per message, so `CkMethodRequest.Caller` proves nothing. A
  service that opts in with `AddCkMethodCallerTokenValidation(o => o.Authority = <authority of its HTTP API>)` (section `Methods:Token`)
  makes `CkMethodHandlerInvoker` (bus path only) validate `CallerAccessToken` **before the parameters are interpreted** and expose the
  result as `CkMethodContext.CallerPrincipal` (`CkMethodPrincipal` + `ClientId`, `TenantId`, `Subject`, `IsServiceToken`; the three-argument
  constructor stays). `ICkMethodCallerTokenValidator.ValidateAsync(token, tenant, ct)` / `ValidateAsync(request, ct)` is public for hosts
  that handle requests themselves (the pipeline handler, AB#7034) and returns `CkMethodTokenValidationResult` (`Principal` or `Failure` =
  `METHOD_FORBIDDEN` + `reason`). Reasons (`CkMethodTokenReasons`, additive): `token-missing`, `token-expired`, `token-invalid`,
  `tenant-not-allowed`, `audience-mismatch`; a missing `octo_api` scope, a service token without `client_id`, an unknown issuer/key and
  `alg: none` are all `token-invalid` (the answer never names the claim). Settings mirror the HTTP API: `Authority` (issuer = authority with
  trailing slash, keys from `{Authority}.well-known/openid-configuration` JWKS), `ValidateAudience` **off** unless `ValidAudiences` is set
  (then one of them is required; with it empty every `octo_api` token of the tenant is accepted), scope `octo_api`, clock skew 30 s,
  asymmetric algorithms only (HS* and `none` can never be configured in). Tenant rule = `TenantAuthorizationMiddleware`, always enforcing:
  `tenant_id` must equal the request tenant (case-insensitive); `allowed_tenants` is never consulted (it is a selection hint); no `sub` =
  service token, needs `client_id`, accepted for the request tenant or when listed in `TenantAuthorization:CrossTenantServiceClientIds`
  (shared options; the `LogOnly`/`Disabled` modes of the HTTP side are never applied); no parent-tenant rule. `token-expired` is only
  reported for a token that is genuine otherwise (re-validated without the lifetime/audience check; expired + forged = `token-invalid`;
  expired + foreign tenant = `tenant-not-allowed`). Keys: own cache (`TimeProvider`), scheduled refresh `KeyAutomaticRefreshInterval` (12 h)
  keeps the cached keys when the identity service is down, an unknown `kid` forces one refresh throttled by `KeyRefreshInterval` (5 min,
  counted from the last download). No keys at all (identity unreachable, no authority) = `CkMethodTokenKeysUnavailableException` ->
  `METHOD_HANDLER_FAILED` (the caller did nothing wrong), never an accepted token. Nothing from the token (value, claim, key id) reaches a log
  line, exception or failure; logs carry tenant, reason and an internal category only (log capture tests). `DisableValidation` is a test
  configuration: unverified claims, tenant and scope rules still apply, and `IValidateOptions` + `ValidateOnStart` fail the start outside
  `Development`. **In-process**: `CkInProcessMethodTransport` builds its invoker without a validator and hands the principal that the calling
  process established (`ICkMethodPrincipalProvider`, default `CkMethodPrincipalScope`) to the handler as `CallerPrincipal`, read in the
  caller's flow before the detached run. Without the opt-in a remote handler has no validated caller (`CallerPrincipal == null`) and the host refuses every call (next entry).
- **Method host enforces the declared authorization** (AB#7664, finding F-1 of the adversarial suite AB#6915; `CkMethodHandlerInvoker`, tests `tests/Methods.Tests/Authorization/CkMethodHostAuthorizationTests.cs`, adversarial C16): after the caller token validation (remote) or with the principal of the calling process (in-process) and **before the parameters are deserialized**, the invoker calls `ICkMethodAuthorizationEvaluator.Evaluate(definition.Authorization, definition.Kind, request.TargetRtId, principal)` - the same evaluator type the gateway uses, no copied rule - on every path that reaches a handler. A registration without `Definition` is refused (`UnknownMethod`); a missing principal is refused. A refusal is `METHOD_FORBIDDEN` / `not-authorized` (`CkMethodAuthorizationErrors.Forbidden`), byte-identical to the gateway, the handler and the target are not touched; the log names the denial (`CkMethodDenial`) only. `AddCkMethodHandlers` and `AddCkInProcessMethodHandlers` `TryAdd` the evaluator and bind `Methods:Authorization`; the `CkMethodHandlerInvoker` constructor takes the evaluator as a required argument. Tests that are not about authorization register `AllowAnyCallerEvaluator` (integration) or use the fixtures' administrator principal (`HostFixture.Administrator`). Hosts in other repositories (pipeline handler AB#7034, adapters through the SDK) pick this up with the library update and must enable `AddCkMethodCallerTokenValidation`.
- `CkMethodRequest` carries `Caller` and `CallerAccessToken` unchanged to the handler; neither reaches a log (`ToString()` redacts,
  the redactor masks the token in exceptions).
- **Redacted request view** (AB#6914, F3.4; `src/Methods/Redaction/`, tests `tests/Methods.Tests/Redaction`,
  `tests/Methods.IntegrationTests/CkMethodRedactionBusTests.cs`): audit is outermost (band 100) and sees the request before
  authorization, so redaction happens **in front of the pipeline, not as a filter**. `CkMethodInvocationContext.Request` is a *view*:
  the values of the CK `sensitive: true` parameters are replaced by `"***"` (whole value, also nested objects/arrays; names match
  case-insensitively), `CallerAccessToken` is `null`, everything else is as built. An unparsable or non-object payload becomes `{}`;
  a method the definition source does not know (or no source at all) is **fail-closed**: every parameter value is masked. The
  raw request goes only to the transport: when a filter calls `next(r)`, the pipeline puts the raw `ParametersJson`/token back.
  An edited `ParametersJson` is **merged per member** (AB#7016, `CkMethodRequestRedaction.TryRestore`): a sensitive member still
  `"***"` is restored from the raw request, a sensitive member the filter gave another value (or removed) is honoured, and a `"***"`
  for a sensitive member the raw request lacks is refused with `METHOD_VALIDATION_FAILED` (tests `FilterParameterEditTests`). Merge
  was chosen over refusing every edit that touches a sensitive member: it is the least surprising (the filter's intent is kept, the
  secret never travels through the filter) and still fails closed. Consequence for F3.5: a filter cannot hash or compare the real parameter values (an
  idempotency fingerprint over the view would equal for different passwords). The sensitivity comes from
  `ICkMethodSensitivityResolver` (registered by `AddCkMethodInvocationPipeline`; reads `ICkMethodAuthorizationDefinitionSource`, else the CK
  cache). `CkMethodRedactor.For(sensitiveNames|null, request)` masks the same values (plain, JSON-escaped in all common encoder variants, JSON-in-JSON, percent-encoded; AB#7015, `SecretForms`) in exceptions the pipeline and the dispatcher log
  (a bus exception that quotes the payload).
  **Per-invocation facts (AB#7816; `CkMethodInvocationFacts`, `src/Methods/InProcess/CkMethodInvocationFacts.cs`, tests `InvocationFactsTests` and `CkMethodDispatcherTests.Facts_*`)**:
  `CkMethodInvocationContext.Facts` is created by the pipeline once per call and shared by all filters of that call (the `with { Request = r }`
  copy keeps the reference), so an inner filter hands facts to an outer one, read after `next()` returns. Members: `ReplayOf` (Guid?, set only by
  the idempotency filter at band 300 when it answers from the store; the audit filter at 100 reads it) and `ParametersFingerprint` (opaque
  `CkMethodParametersFingerprint`: HMAC bytes plus key id; `internal set`, filled by the pipeline (AB#7819); compared in constant time with
  `Matches`). Never on the wire (not a member of the request or the response), never serialized; `ToString()` of the facts, the fingerprint and the
  context print `[redacted]` instead of the value. A context a test builds by hand starts with its own empty facts.
  **Parameters fingerprint (AB#7819, OD-4 row 2 + G1; `CkMethodParametersFingerprinter` / `ICkMethodParametersFingerprinter` in `src/Methods/InProcess/`, options `CkMethodFingerprintOptions` in `src/Methods/Idempotency/`, tests `ParametersFingerprintTests`)**:
  the pipeline computes it from the **raw** request before the redacted view is built, only for a call that carries `IdempotencyKey` (an unkeyed call computes nothing) and only when the
  fingerprinter is registered (`AddCkMethodIdempotencyOptions()` registers it, so a host without an idempotency store pays nothing and refuses nothing). Input: domain tag, `ckTypeId`, `targetRtId`, canonical parameters
  (members sorted ordinal, duplicate member: last wins, numbers normalized `1` = `1.0` = `1e0`, array order kept, null or empty = `{}`), each part length-prefixed. HMAC-SHA-256 with the
  current key, key id stored with it; `Facts.AlternateParametersFingerprints` holds the same request under the previous key (and, for a method without sensitive parameters, the unkeyed
  `sha256`), `Facts.MatchesStoredFingerprint(stored)` verifies a stored fingerprint of any of them in constant time. No key and a method with sensitive parameters (or an unknown method, fail closed):
  the pipeline inserts a refusal step between authorization (200) and idempotency (300): `METHOD_VALIDATION_FAILED`, field `idempotencyKey`, reason `fingerprint-key-missing`; audit (100) sees it,
  the idempotency filter and the handler do not run. No key and no sensitive parameters: unkeyed SHA-256, key id `sha256`. Configuration `Methods:Idempotency:Fingerprint:Current:{Id,Secret}` and
  `:Previous:{Id,Secret}` (secret = base64 of at least 32 bytes, id `[A-Za-z0-9._-]{1,64}`; malformed or half-set keys fail the start, messages name the setting, never the value; keys are read at start;
  all replicas share them). The key options and the fingerprint never appear in logs, exceptions, `ToString()` or audit (canary test). Ops prerequisite: a Vault + Helm secret per environment (Heimdall).
  **Redacted response view (AB#7007; `CkMethodResponseRedaction` in `CkMethodSensitivity.cs`)**: the mirror of the request side. When
  the CK result of the method is `sensitive` (`ICkMethodSensitivityResolver.IsResultSensitive`; fail-closed `true` for an unknown method,
  a missing source or a faulting source), the terminal step keeps the raw response and hands the filters a view whose `ResultJson` is
  the JSON string `"***"` (still valid JSON; only a present result is masked, error and accepted responses and `Success`/`Operation`/ids
  are untouched). The outermost step restores the raw `ResultJson` when the response that comes back from the chain still carries the
  placeholder, so the **caller gets the real result**; a filter that returns another response (failed, another result) keeps its
  change, and a filter that answers without calling `next` is returned as is. The view exists only in-process; the wire envelope and
  `ContractVersion` are unchanged. Consequence for F3.5: an idempotency store behind the filters holds the masked result, so how a
  replay answers is decided there. Not covered: per-attribute masking inside a record result (the marker is whole-result), pipeline
  handlers (they refuse a sensitive result, see the controller), and what a handler itself writes. The `CK method handle` span carries only ids and the
  transport name, never a result. The canary tests listen to all activity sources, so a new source cannot leak a result unnoticed.

- **Adversarial suite (AB#6915, F3.4b exit)**: a repeatable attack suite over the real method path, recorded results and the
  list of findings and accepted limitations in `docs/method-adversarial-suite.md`. Code: `tests/Methods.IntegrationTests/Adversarial/`
  (`CkMethodAdversarialBusTests`, trait `Category=Adversarial`; private RabbitMQ container, a fake identity service, no shared
  stack). Three parties on one broker: a remote handler host with caller token validation, a gateway (dispatcher + authorization
  filter + audit probe) and an attacker that publishes crafted requests straight to the method queue. **One command**:
  `dotnet test tests/Methods.IntegrationTests -c DebugL --filter "Category=Adversarial"` (Docker needed); the run writes the
  result table to `adversarial-results.md` next to the test assembly. A case that documents a gap is pinned by a `C..._Gap_`/
  `_Limitation_` test (current behaviour) plus a skipped test with the expected behaviour; when a gap is closed the pin turns
  red, the skipped test is enabled. The asset-repo half (HTTP token layer, gateway, GraphQL) is in
  `octo-asset-repo-services/tests/**/Adversarial`, see its CLAUDE.md. A new method path (filter, transport, handler host) gets
  its cases added here before it ships.

### Method dispatcher (AB#6905, F3.3)

`ICkMethodDispatcher` (`src/Methods/Dispatching/`, registration `AddCkMethodDispatcher()` + `AddCkMethodHandlerRegistryFeed()` in the
`AddDistributionEventHub` configuration) is the calling side of a CK method. Guide: `docs/method-handler-sdk.md` section 9.

- **Order**: resolve the declaring element (`CkCacheMethodDeclaringElementResolver`: `CkTypeGraph.AllMethods` of the tenant, rewrites
  `MethodId` to the qualified id of the declaring type; inherited and interface methods route to the declaring queue; unknown = as
  given) -> in-process transport when `ICkInProcessMethodTransport.CanHandle` -> otherwise `ICkMethodInvocationPipeline.ExecuteAsync("bus",
  request, send, ct)`. **The dispatcher adds no filter and no pipeline**; `send` is one mandatory, non-persistent
  `GetCommandResponseAsync` (never resent). It never throws for a failing call; only caller cancellation surfaces.
- **Error mapping**: `CommandNotRoutableException` -> `METHOD_HANDLER_NOT_AVAILABLE` (`methodId`, `reason` `no-live-handler`);
  `DistributionTimeoutException` / deadline passed -> `METHOD_TIMEOUT`; `DistributedOperationFailedException`, bus-side cancellation,
  resolver/DI faults -> `METHOD_HANDLER_FAILED` + `correlationId`. No exception text on the wire.
- **Timeout** (AB#6906): `NormalizeTimeout` runs first in `InvokeAsync` (before routing, filters and the in-process/bus branch): it cuts
  `DeadlineUtc` to `now + MaxTimeout` (10 min, also the deadline the handler sees) and fills `CkMethodRequest.TimeoutSeconds`
  (`ceil(remaining)`, 1-300) when the caller left it out. The bus send waits `DeadlineUtc - now + ResponseGrace` (2 s, the documented
  tolerance), TTL the same, and clamps again (a filter may set a later deadline). `ICkMethodTimeoutResolver` (public, `AddCkMethodDispatcher`;
  `CkMethodTimeoutResolver.cs`) is how a gateway gets the deadline: effective `execution.timeoutSeconds` from the CK cache
  (`CkMethodGraph.TimeoutSeconds`, 15 s default, clamped 1-300; unknown method or cache fault = default, never throws). Clamping rules
  live in internal `CkMethodDeadline`. `timeoutSeconds` of every `METHOD_TIMEOUT` is the request's `TimeoutSeconds`.
- **No retry** (AB#6907): one `GetCommandResponseAsync` per call, never resent; every transport fault is `METHOD_TIMEOUT`,
  `METHOD_HANDLER_NOT_AVAILABLE` or `METHOD_HANDLER_FAILED`. Duplicates: see the bus transport above. Not detected (no "at most once"):
  new invocation id, other instance without broker flag, restarted instance, single instance that loses its connection. A shared
  duplicate store / idempotency store is open decision OD-3/OD-4 (`methodConsistency.md`) and is **not** built; do not add one here.
- **Registry** (`ICkMethodHandlerRegistry`, in-memory, advisory): fed by announce/withdraw, expiry = receipt time + TTL (dispatcher
  clock), withdrawal beats older announcements, `MarkNoQueueBound` spares instances announced during the send. Used for the tenant check
  (`tenant-not-served`) and the contract version check (never send a newer version than announced); it never vetoes a send, the broker's
  mandatory flag decides. `CkMethodDispatcherStartupService` (order 30) publishes a discovery request at start. Diagnostics: see below.
- **Registry snapshot** (AB#5671): with an `ICkMethodHandlerSnapshotStore` in the host (port and Mongo store live in
  `octo-construction-kit-engine-mongodb`, plain collection `ck_method_handler_snapshot` in the system DB, TTL index) the registry queues
  every change (`CkMethodHandlerRegistry.TakeChanges`) and `CkMethodHandlerSnapshotFlushService` writes it every
  `Methods:Dispatcher:SnapshotFlushInterval` (5 s); `CkMethodHandlerSnapshotLoadService` (order 28, before the discovery request) restores
  it (`Restore`). A row is **last seen, not live**: live only inside `ExpiresUtc` (receipt time + TTL), withdrawal = tombstone with the
  source timestamp, restored times are capped (`LastSeenUtc` <= now, expiry <= last seen + 24 h, older than 24 h skipped), received
  announcements win. Rows are service data only (allow-list test). No store or `SnapshotEnabled=false` = in memory as before; a failing
  store never affects a call (requeue, retry next flush). Tests: `tests/Methods.Tests/Dispatching/CkMethodHandlerSnapshotTests.cs`.
- **Registry diagnostics** (AB#7836): `ICkMethodHandlerRegistryDiagnostics` (public, `AddCkMethodDispatcher`; implementation
  `CkMethodHandlerRegistryDiagnosticsService`) returns the dispatcher instance id, every entry with `Source` (`Announcement` | `Snapshot`;
  `Restore` sets `Snapshot`, any received announcement or withdrawal sets `Announcement`, not persisted) and `State`
  (`CkMethodHandlerEntry.GetState(now)`: `Live` | `Expired` | `Withdrawn` | `NoQueueBound`, same rules as `GetLiveHandlers`), the snapshot
  health from `CkMethodHandlerSnapshotPersistence` (`Enabled`, last load time/rows read/restored, last flush success, last flush failed +
  exception **type name only**) and the method ids of `ICkInProcessMethodTransport` (transport `in-process`). Service data only (allow-list
  test). Tests: `tests/Methods.Tests/Dispatching/CkMethodHandlerRegistryDiagnosticsTests.cs`. The GraphQL query is AB#7837 (asset-repo).
- **Tests**: `tests/Methods.Tests/Dispatching`, `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.cs` (the integration project
  now references FakeItEasy for a fake `ICkCacheService`).

### Replica sharing and dispatch overhead (AB#6908, F3.3)

Tests `tests/Methods.IntegrationTests/CkMethodReplicaSharingBusTests.cs` and `CkMethodQueueArgumentsBusTests.cs`; report with numbers,
setup and the repeat command: `docs/method-performance.md`. Replicas of one service share the one non-exclusive method queue (each call
executed exactly once, spread proven for 2 and 3 replicas; a gracefully stopped replica loses no call). The latency test (category
`Performance`, no-op handler, sequential) asserts p95 < 250 ms by default and p95 < 20 ms with `OCTO_METHOD_PERF_STRICT=1`; measured p95
is about 1 ms (1 or 3 replicas, Apple M4 Pro, loopback). A shared method queue must never change its arguments in place (the new build
would fail its start with `PRECONDITION_FAILED` behind an `EndpointReadyTimeout` `TimeoutException`; the old replicas keep serving): a
change needs a new queue name.

## Artifact store: nested keys and the archive copy staging location (AB#5967 / AB#6969)

`src/ArtifactStorage` (package `Meshmakers.Octo.Services.ArtifactStorage`, docs: `README.md`, "Artifact storage configuration" and
"Key layout and categories") is the platform artifact store. Native archive dump and restore (CrateDB `COPY TO` / `COPY FROM`,
concept `octo-construction-kit-engine/docs/concept-tenant-backup-native-archive-dump.md`) use it as the staging area:

- **Nested keys.** `ArtifactKeyBuilder.BuildNested` / `RunPrefix` / `TryParseNested` build and parse
  `<instancePrefix>/<category>/<tenantId>/<runId>/<segment>[/…]`; category `ArtifactCategories.CopyStaging` (`copy-staging`).
  The strict three-segment `TryParse` is unchanged and rejects a nested key. The providers need no change: they list, delete and
  expire by plain key prefix (`ArtifactStoreContractTests.NestedKeys_*` runs against file system, MinIO and Azurite). The category
  needs a lifecycle rule on each store (expiry one day); that is environment set-up, not code of this repo.
- **Staging location.** `IArchiveCopyStagingLocationFactory` (`Staging/`) creates `ArchiveCopyStagingLocation` (type and
  redaction live in the engine, `Runtime.Contracts/StreamData`) for S3 and Azure; FileSystem and an Azure store without account key
  report `IsSupported = false` with a reason, `Create` then throws `ArchiveCopyStagingNotSupportedException`. The project
  references `Meshmakers.Octo.Runtime.Contracts` for the type (open design question: a neutral location type would avoid the
  dependency on the large package; the engine contract was the lane's input).
- **Options.** `S3:CrateDbServiceUrl` (`OCTO_ARTIFACTSTORAGE__S3__CRATEDBSERVICEURL`, the second endpoint as CrateDB sees it) and
  `AzureBlob:StagingSasLifetime` (default 2 h). Validation names the option, never a value.
- **S3 secret with `:`.** Verified against CrateDB 5.9.10 + MinIO: `/`, `+`, `@` survive percent-encoding, a `:` does not
  (`SignatureDoesNotMatch`: CrateDB splits the decoded user info at the first colon). The factory reports such keys as not
  supported instead of letting `COPY` fail with a signature error.
- **Azure SAS.** Container scope, per run, `Staging/` + `AzureBlob/AzureStagingSas.cs`; dump create/write/list, restore read/list;
  managed identity = phase 2. Azurite proves accepted and refused operations (`ArchiveCopyStagingAzuriteTests`).
- **Canaries.** `ArchiveCopyStagingLocationFactoryTests` assert that key, secret (every spelling) and `sas_token` appear in no
  `ToString()`, property, JSON serialization, `Scrub`bed text, factory log line or exception message. Proven red by mutation: the
  factory logging `RevealUri()`, the secret or the token placed in the URI path, a connection string echoed into the reason.
  Never log or serialize `RevealUri()` / `RevealOptions()` in new code.

## Tests

`tests/Infrastructure.Tests` — xUnit + FakeItEasy. `Infrastructure.csproj` exposes internals to this
assembly via `<InternalsVisibleTo Include="Infrastructure.Tests" />` so internal middleware such as
`TenantMiddleware` can be unit-tested directly (see `Middleware/TenantMiddlewareTests.cs`).

## Cache streams: missing file is `null` (AB#7256)

`OctoRepository` (`src/Infrastructure/DistributionEventHub`, the `IRepository` of `IDistributedCacheService`) returns
`null` from both `DownloadBinaryAsync` overloads when the cache file does not exist (it used to leak the repository's
`EntityNotFoundException` for `IDownloadStreamHandler`). `DistributedCacheService.GetCacheStreamByIdAsync` therefore
returns `null`, and callers such as the `octo-bot-services` import jobs raise a typed error naming tenant and key.
The transaction is aborted on a miss. Read-your-writes of the underlying GridFS read lives in
`octo-construction-kit-engine-mongodb` (primary read on a miss).

## Publishing the system CK model: explicit release decision (AB#7433)

A merge to `main` does **not** publish `System.Notification` to the CK catalogs. The pipeline parameter `publishSystemCkModels`
(boolean, **default false**; same name and semantics as AB#7343 in `octo-construction-kit-engine`, AB#7432 in the
communication controller) feeds `publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0`). A main build still
runs ValidateVersion, the schema compile and the cascade check, logs `CK publish on main is OFF (publishOnMain=false)` and
publishes nothing. Reason: a new System* version breaks exact-pinned dependents on live tenants until they are re-pinned.

**To publish explicitly** (needs the release decision of the platform owner): Azure DevOps -> pipeline `octo-common-services-CI` ->
*Run pipeline* -> branch `main` -> tick *Publish System CK models to the CK catalogs (main)*; CLI:
`az pipelines run --id 90 --branch main --parameters publishSystemCkModels=true`. `r*` tags (release train) publish as
before; `dev/*`/`test/*` builds never publish to the shared catalogs. A merge that bumps the model therefore leaves the version
unpublished until that run; repos that restore it from the catalog must wait for it.
