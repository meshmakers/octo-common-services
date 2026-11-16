---
description: HTTP surface every host shares: /metrics and /health, tenant middleware, tenantId route constraint, CORS and the token cookie.
applies_to: src/Observability/**, src/Infrastructure/Routing/**, src/Infrastructure/Cors/**, src/**/TenantMiddleware.cs, src/Infrastructure/Middleware/Cookie*.cs
---

# HTTP pipeline

## `/metrics` is internal-only

`MapObservability()` serves the Prometheus scrape endpoint `/metrics` only to connections that arrive on
the port named by `Observability:PrometheusPort` (env `Observability__PrometheusPort`). Unset or `0` (the
default) means the endpoint does not exist (404) — metrics reach Dash0 through the OTLP exporter, nothing
scrapes `/metrics` today. The guard is the socket's local port, never the `Host` header (caller-controlled).

The port must be a Kestrel listener that the Service and the Ingress never route to (chart: container port
only, e.g. `ASPNETCORE_URLS=http://+:80;http://+:9464`). The endpoint used to sit on the main port and was
reachable unauthenticated through every public ingress, with tenant ids, pipeline names and pool ids in
the output. Do not map `/metrics` (or `MapPrometheusScrapingEndpoint`) on the main listener again;
`PrometheusEndpointTests` fails if you do.

## `/health` needs a Bearer token

`/health` writes the full health report (check names, status, descriptions, CPU/memory figures), so
`MapObservability()` lets only callers through that authenticate with the `Bearer` (JWT) scheme; anonymous
callers get `401` with `WWW-Authenticate: Bearer` and no body. Any valid token is enough, no role. The
check runs inside the endpoint (`WebApplicationExtensions.IsAuthenticatedAsync`), not through
`RequireAuthorization()`: it must not depend on `UseAuthorization()` (the mesh adapter never calls it) and
it fails closed (401) on hosts that register no `Bearer` scheme (EDA and FinAPI adapters). The browser
consumers (Studio / MeshBoard `HealthService`) send the user's token. `/healthz/live` and
`/healthz/ready` stay anonymous endpoints that return a plain-text status body for the kubelet probes. `HealthEndpointTests` fails
if `/health` becomes anonymous again.

## Multi-Tenancy Middleware

`UseOctoTenants()` (`src/Infrastructure/Configuration/ApplicationBuilderExtensions.cs`) registers
`TenantMiddleware` (`src/**/TenantMiddleware.cs`). For every tenant-scoped
route it resolves the tenant repository (404 if the tenant does not exist) and stores it in
`context.Items`. When the hosting feature `CanBeEnabled()` and is NOT enabled for the tenant, the
middleware short-circuits with **403 Forbidden**.

### Enabled-gate exemptions

The 403 enabled-gate is skipped for:

- The exact `SystemEndpoints` paths: `/system`, `/signin-oidc`, `/healthz`.
- **Feature lifecycle endpoints** — any path ending in `/enable` or `/disable`
  (case-insensitive). These endpoints manage the enabled-state itself, so gating them would make a
  disabled feature impossible to re-enable via its own API (a past regression: tenant-scoped
  `POST {tenantId}/v1/reporting/enable` returned 403 once reporting was disabled). Tenant resolution
  (404-if-missing + `context.Items`) still runs for these paths — only the enabled-check is skipped.

This relaxation is safe for all consumers: the gate only fires when `CanBeEnabled()` is `true`, and
services with no toggleable feature (e.g. Identity Services) never define `/enable` `/disable`
tenant routes, so their behaviour is unchanged.

## The `{tenantId:tenantId}` route constraint

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

## CORS policy and the access-token cookie

- `CorsPolicyProvider` (registered by `AddOctoServiceInfrastructure()`, used by `app.UseCors()` without a policy name) allows
  exactly the registered client origins of the tenant in the first path segment (unknown tenant: system tenant), any header
  and method, **never credentials**. `OctoCorsOptions` (`services.Configure<OctoCorsOptions>(…)`) may add exposed headers
  and a preflight max-age; it cannot widen the origin list. bot-services uses it for the tus headers.
- `UseOctoCookieBasedAuthentication(path)` turns `?jwt_token=` into the Authorization header and the `OctoIdentityAccessToken`
  cookie **only below `path`** (default `/ui`, the Hangfire dashboard of bot-services; the root is refused). The cookie is
  HttpOnly, Secure, `SameSite=Strict`, `Path = PathBase + path`. Outside the path neither the query token nor the cookie is
  used, so API routes stay bearer-header only (`TenantAuthorizationMiddleware` no longer sees cookie-injected headers there).
  Consumers: bot-services (`/ui`); identity-services calls the parameterless overload but serves nothing below `/ui`.
- Tests: `tests/Infrastructure.Tests/Cors/KnownOriginsCorsPolicyTests.cs` (TestServer: unknown origin → no ACAO/ACAC on
  200/401/404/500 and preflight, known origin → ACAO + exposed headers, tenant-specific origins, no ACAC on bearer routes) and
  `tests/Infrastructure.Tests/Middleware/CookieBasedAuthenticationMiddlewareTests.cs` (Set-Cookie attributes, path scope).
