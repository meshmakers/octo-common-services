---
description: Tenant generation check: tenant_gen claim vs. the tenant's creation time, outcomes, enforcement mode, cache, metrics (also tenant check decisions), public API.
applies_to: src/Infrastructure/TenantGeneration/**, tests/Infrastructure.Tests/TenantGeneration/**, src/Methods/Authorization/CkMethodCallerTenantCheck.cs
---

# Tenant generation check (`tenant_gen`)

When a tenant is deleted and a new tenant with the same id is created, an access token that was
issued for the deleted tenant and has not expired yet must not be accepted by the new tenant. Access tokens are
self-contained JWTs validated offline, so dropping the tenant database does not affect them.

Identity stamps every **access token** with `tenant_gen`: the
`TenantLifecycleRecord.CreatedUtc` of the token's `tenant_id` as Unix milliseconds, written as a decimal string. A new
creation cycle of a tenant id sets a new value; a restore over the existing tenant keeps it. This package compares the
claim with the current generation of the tenant.

Rollout order: identity with the claim → this check in `LogOnly` → identity refuses old grants → `Enforce` per environment.

## Where it runs

| Caller | Transport label | Refusal in `Enforce` | Failed lookup in `Enforce` |
|---|---|---|---|
| `TenantAuthorizationMiddleware` (every host of `UseOctoTenantAuthorization()`) | `http` | `403` with `{"message": ...}` | `503` with `{"message": ...}` |
| CK method bus host (`CkMethodCallerTokenValidator`, `AddCkMethodCallerTokenValidation`) | `method` | `METHOD_FORBIDDEN`, reason `tenant-not-allowed` | `METHOD_HANDLER_FAILED` (`CkMethodTenantGenerationUnavailableException`) |
| CK method in-process transport (`CkMethodCallerTenantFilter`) | `method` | `METHOD_FORBIDDEN`, reason `tenant-not-allowed` | `METHOD_HANDLER_FAILED` + `correlationId` |
| identity grants (octo-identity-services) | `grant` (+ `grant_type`) | `invalid_grant` | `server_error` |

Both method transports run the same step, `CkMethodCallerTenantRule.CheckGenerationAsync`, after the tenant rule allowed the call.

## The rule

- **Compared against the token tenant.** The generation is always looked up for the token's `tenant_id`, never for the
  route or call tenant. That covers the REST parent path (`[AllowParentTenantAdministration]`): a parent token
  on a child route is checked against the parent's generation. A later rule that admits parent administrators on child
  tenants via methods calls the same check unchanged.
- **Exact comparison.** The current generation is formatted like `OctoTenantGenerationClaimHandler.FormatGeneration`
 : `CreatedUtc` normalised to UTC (`Utc` as is, `Local` converted, `Unspecified` taken as UTC), Unix
  milliseconds by integer truncation, invariant decimal string (`TenantGenerationFormat.Format`). The claim is compared
  ordinally. No tolerance window, no clock comparison. Shared test vector: `2026-11-15T13:46:21.540Z` → `"1794750381540"`.
- **Not well formed = mismatch.** A claim that is not a JSON string, appears more than once, or is not canonical (sign,
  leading zero, whitespace, decimal point, exponent, non-ASCII digits, more than 19 digits) is `mismatch`.
- **Access tokens only.** The claim is read from the bearer access-token principal. ID tokens and userinfo carry no
  `tenant_gen`; cookie principals, which the middleware already skips, are not checked.

### Outcomes

| Outcome | When | `LogOnly` | `Enforce` |
|---|---|---|---|
| `match` | `tenant_gen` equals the current generation | allowed | allowed |
| `missing` | the tenant has a generation, the token carries no `tenant_gen` (tokens issued before identity stamped the claim) | allowed, logged, counted | refused |
| `mismatch` | different or not well formed (a token of an earlier tenant with the same id) | allowed, logged, counted | refused |
| `not_checked` | no `tenant_id`; the **system tenant** (`OctoSystemConfiguration.SystemTenantId`, never refused, with or without a record and whatever its tokens carry — its `tenant_gen` may differ after the system database was recreated); a tenant without lifecycle record or `CreatedUtc` (legacy); a host without `ITenantLifecycleStore` | allowed | allowed |
| `lookup_failed` | the lifecycle record could not be read | allowed, counted (LogOnly must not add an outage) | `503` / method error, never `200` |

**Cross-tenant service clients** (`TenantAuthorization:CrossTenantServiceClientIds`, route ≠ `tenant_id`) keep today's
routing rule; their token tenant (normally the system tenant) is not checked and nothing is counted. On their own tenant
they are checked like every other service token. Client-credentials tokens (no `sub`) are checked against their own
`tenant_id` exactly like user tokens.

## Option

`TenantAuthorization:TenantGenerationEnforcement` = `Disabled` | `LogOnly` | `Enforce`
(env `OCTO_TenantAuthorization__TenantGenerationEnforcement`), **default `LogOnly`** in this release. Bound by
`AddOctoTenantAuthorization(configuration)` like the other tenant authorization options; the method registrations bind
the same section. One value per environment therefore switches HTTP, methods and identity's grants together.

- `Disabled`: no lookup, no log, no metric.
- `LogOnly`: request outcomes never change. `missing` and `mismatch` are logged as a warning naming client, tenant,
  outcome and transport (never the `tenant_gen` value or the token), at most once a minute per client, outcome and
  transport; every check is counted.
- `Enforce`: see the table above.

`TenantAuthorization:TenantGenerationCacheDuration` (default `00:00:30`) is the cache TTL, below.

The switch to `Enforce` per environment and the later code default flip follow the operator plan
[tenant-generation-enforce-plan.md](tenant-generation-enforce-plan.md).

## Cache: no MongoDB round-trip per request

`TenantGenerationReader` (singleton, `ITenantGenerationReader`) caches the generation per service instance and tenant,
including the "no record" result:

- at most one lifecycle read per tenant per TTL window per instance; concurrent misses for one tenant share one read;
- a failed read is answered as failed for 2 s (`TenantGenerationReader.FailureBackoff`, counted from the moment the read
  failed, so a slow read that times out gets the full back-off), then read again; a MongoDB outage costs one read per
  tenant per back-off, not one per request;
- the tenant events drop the entry where the host receives them: `PosCreateTenant`, `PosUpdateTenant`
  (`PosCreatePosUpdateTenantConsumer`), `PreUpdateTenant`, `PreDeleteTenant` (`PreUpdatePreDeleteTenantConsumer`);
- on a `mismatch` whose token generation is **newer** than the cached one, and on a token with a claim for a tenant
  cached as "no generation", the record is read once more before deciding (the tenant may have been created after the
  value was cached); the decision then uses the fresh value: still no generation → `not_checked` (allowed), still older
  → `mismatch`. The one-second rule only throttles these forced reads (a forced read is skipped while the cached value is
  younger than one second, so a stream of such tokens costs at most one read per tenant per second); it does not bound
  how long a "no generation" result is kept, which is the TTL;
- a host without `ITenantLifecycleStore` never reads: every tenant is `not_checked`, with one warning at the first check;
- 🔴 **the TTL must stay below the delete tombstone time** (about 90 s, the settle sweep of `ReconcileDeletingTenantsAsync`).
  A deleted tenant id can only be created again after the tombstone is gone, so with a TTL below that no cached generation
  of the deleted tenant survives into the recreated tenant, even on an instance that missed the events;
- a configured TTL above 60 s (`TenantGenerationReader.MaxCacheDuration`) is clamped to 60 s with one warning; `00:00:00`
  disables the cache;
- capped at 1024 tenants.

A restore of a tenant keeps `CreatedUtc`, so its tokens stay valid.

## Metric

Counter `octo.tenant_auth.generation_checks` on meter `Meshmakers.Octo.TenantAuthorization` (`TenantGenerationMetrics`,
registered in `ObservabilityBuilder`). Labels:

| Label | Values |
|---|---|
| `outcome` | `match`, `missing`, `mismatch`, `not_checked`, `lookup_failed` |
| `mode` | `log_only`, `enforce` |
| `transport` | `http`, `method`, `grant` |
| `client_id` | the token's `client_id` (a client registered at identity, the token is validated); `unknown` when absent or not printable ASCII / longer than 128 characters; `other` after 1000 distinct values per process |
| `grant_type` | only when identity passes one, a fixed set (`TenantGenerationMetrics.GrantTypes`): `token_exchange`, `on_behalf_of`, `refresh_token`, `authorization_code`, `device_code`, `client_credentials`, `password`; anything else `other` |

Never the tenant id, the user, `sub`, `tenant_gen` or a token value. A unit test asserts the exact label set.

### Decisions of the tenant check

Counter `octo.tenant_auth.decisions` on the same meter (`TenantAuthorizationMetrics`, `src/Infrastructure/Middleware/`)
counts every decision of the tenant check of `TenantAuthorizationMiddleware` that runs before this one (token tenant
against route tenant, [tenant-authorization.md](tenant-authorization.md)), next to its log line. The `LogOnly` inventory
of the user and the service path can so be read per service, client and endpoint without searching log text. Both
counters share the `client_id` bounds (`ClientIdLabelBudget`, `MetricLabels`).

| Label | Values |
|---|---|
| `outcome` | `match` (token tenant = route tenant, also for an allow-listed client on its own tenant), `allowed_parent_admin` (parent-tenant rule), `allowed_cross_tenant_client` (service allow-list, another tenant), `not_checked` (service path in `Disabled`), `would_deny` / `would_deny_missing_tenant_id` (`LogOnly`: `Enforce` would refuse it; the second when the token has no `tenant_id`), `denied` / `denied_missing_tenant_id` (`Enforce`: 403) |
| `token_kind` | `user` (token with `sub`), `service` (client credentials) |
| `mode` | the mode of that token kind: `UserTokenEnforcement` (`log_only`, `enforce`) or `ServiceTokenEnforcement` (`disabled`, `log_only`, `enforce`) |
| `client_id` | the token's `client_id`; `unknown` when absent or not printable ASCII / longer than 128 characters; `other` after 1000 distinct values per process (same bounds as `octo.tenant_auth.generation_checks`) |
| `route` | on `would_deny…` and `denied…` only: the route **template** of the endpoint with a leading slash (e.g. `/{tenantId}/graphql`), never the request path; `unknown` without a route template, `other` when it is longer than 128 characters or not printable ASCII. On every other outcome `any`: allowed requests are the bulk of the traffic, and client × route on them would multiply the series without serving any gate |

**Series budget.** The OpenTelemetry SDK keeps at most 2000 label combinations per metric by default and folds further
ones into an overflow series without labels. The counter therefore bounds the whole combination: after 1500 distinct
combinations per process a new one is recorded with `client_id` = `other` (and `route` = `other` on refusals) but keeps
`outcome`, `token_kind` and `mode`. The collapsed combinations add at most 9 × 2 × 3, so a refusal is never lost from an
outcome-filtered query. A per-pod series count near 1500 is the sign to look at the clients and routes.
`TenantAuthorizationMetricsSdkTests` checks this against the OpenTelemetry SDK (1000 clients × 2 routes of refusals:
no overflow series, every refusal counted).

Never the tenant id, the user, `sub` or a token value; a unit test asserts the exact label set. Requests the middleware
does not judge are not counted: anonymous endpoints, requests without a bearer header, cookie principals,
unauthenticated requests and routes without a tenant. A request that passes the tenant check is then judged by the tenant
generation check, which has its own counter (above).

The counter is registered by `AddOctoServiceInfrastructure()` and by both `AddOctoTenantAuthorization(...)` overloads and
resolved per request. A host without the registration runs without it and logs one warning (`No TenantAuthorizationMetrics
is registered …`), so a missing counter does not read as a clean inventory.

Typical queries (PromQL):

```promql
# LogOnly inventory: who would be refused, on which endpoint
sum by (service_name, token_kind, outcome, client_id, route) (
  increase({otel_metric_name="octo.tenant_auth.decisions", outcome=~"would_deny.*"}[1d])
)

# Enforce: refusals per caller
sum by (service_name, token_kind, outcome, client_id, route) (
  increase({otel_metric_name="octo.tenant_auth.decisions", outcome=~"denied.*"}[1h])
)
```

Tests: `tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareMetricsTests.cs` (one case per outcome and
mode, label set and bounds, no counting without a route tenant or without the registration).

The gates, Dash0 queries and log lines for the switch to `Enforce` are in
[tenant-generation-enforce-plan.md](tenant-generation-enforce-plan.md).

## Public API for other repositories

All in `Meshmakers.Octo.Services.Infrastructure` (namespace `Meshmakers.Octo.Services.Infrastructure.TenantGeneration`),
already referenced by identity:

- `ITenantGenerationCheck` / `TenantGenerationCheck`: `EvaluateAsync(tokenTenantId, generation)` (outcome only) and
  `CheckAsync(TenantGenerationCheckRequest)` (mode, metric, log; returns outcome and `TenantGenerationDecision`
  `Allow` / `Deny` / `Unavailable`).
- `ITenantGenerationReader` / `TenantGenerationReader`: the cached reader.
- `TenantGenerationClaim.FromPrincipal(principal)` / `FromValue(value, isString)`, `TenantGenerationFormat`.
- `TenantGenerationMetrics`, `TenantGenerationTransports`.
- `services.AddOctoTenantGenerationCheck()`: registers all of them (idempotent). `AddOctoServiceInfrastructure()` and the
  CK method registrations call it.

Identity builds a `TenantGenerationCheckRequest` with `Transport = TenantGenerationTransports.Grant` and a
`GrantType`, and maps `Deny` to `invalid_grant` and `Unavailable` to `server_error`.

## Tests

- `tests/Infrastructure.Tests/TenantGeneration/TenantGenerationCheckTests.cs`: format (shared vector, truncation, kinds),
  canonical form, evaluator, mode matrix, log rate limit, cache (1000 requests = one read, "no record" cached, TTL,
  concurrent misses = one read, failure back-off, TTL clamp, refresh on a newer generation), metric label set and bounds.
- `tests/Infrastructure.Tests/TenantGeneration/TenantGenerationInvalidationTests.cs`: event invalidation, registration.
- `tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareGenerationTests.cs`: HTTP matrix incl. the parent
  path, system tenant, client credentials, cross-tenant client, cookie principal, host without the check.
- `tests/Methods.Tests/Authorization/CkMethodCallerTenantGenerationTests.cs`: match, mismatch, missing, not checked,
  lookup failure and cross-tenant client, each in `LogOnly` and `Enforce`, for the in-process transport and the bus host.
