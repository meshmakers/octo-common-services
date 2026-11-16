# Tenant generation check (`tenant_gen`, AB#8999)

Security item AB#8970: when a tenant is deleted and a new tenant with the same id is created, an access token that was
issued for the deleted tenant and has not expired yet must not be accepted by the new tenant. Access tokens are
self-contained JWTs validated offline, so dropping the tenant database does not affect them.

Since AB#8997 (octo-identity-services #304) identity stamps every **access token** with `tenant_gen`: the
`TenantLifecycleRecord.CreatedUtc` of the token's `tenant_id` as Unix milliseconds, written as a decimal string. A new
creation cycle of a tenant id sets a new value; a restore over the existing tenant keeps it. This package compares the
claim with the current generation of the tenant.

Rollout order: identity with the claim (AB#8997) → this check in `LogOnly` (AB#8999) → identity refuses old grants
(AB#9017) → `Enforce` per environment from the plan of AB#9000.

## Where it runs

| Caller | Transport label | Refusal in `Enforce` | Failed lookup in `Enforce` |
|---|---|---|---|
| `TenantAuthorizationMiddleware` (every host of `UseOctoTenantAuthorization()`) | `http` | `403` with `{"message": ...}` | `503` with `{"message": ...}` |
| CK method bus host (`CkMethodCallerTokenValidator`, `AddCkMethodCallerTokenValidation`) | `method` | `METHOD_FORBIDDEN`, reason `tenant-not-allowed` | `METHOD_HANDLER_FAILED` (`CkMethodTenantGenerationUnavailableException`) |
| CK method in-process transport (`CkMethodCallerTenantFilter`) | `method` | `METHOD_FORBIDDEN`, reason `tenant-not-allowed` | `METHOD_HANDLER_FAILED` + `correlationId` |
| identity grants (AB#9017, octo-identity-services) | `grant` (+ `grant_type`) | `invalid_grant` | `server_error` |

Both method transports run the same step, `CkMethodCallerTenantRule.CheckGenerationAsync`, after the tenant rule of
AB#8711 allowed the call.

## The rule

- **Compared against the token tenant.** The generation is always looked up for the token's `tenant_id`, never for the
  route or call tenant. That covers the REST parent path (`[AllowParentTenantAdministration]`, AB#5060): a parent token
  on a child route is checked against the parent's generation. A later rule that admits parent administrators on child
  tenants via methods (AB#8986) calls the same check unchanged.
- **Exact comparison.** The current generation is formatted like `OctoTenantGenerationClaimHandler.FormatGeneration`
  (identity a27681eb): `CreatedUtc` normalised to UTC (`Utc` as is, `Local` converted, `Unspecified` taken as UTC), Unix
  milliseconds by integer truncation, invariant decimal string (`TenantGenerationFormat.Format`). The claim is compared
  ordinally. No tolerance window, no clock comparison. Shared test vector: `2026-11-15T13:46:21.540Z` → `"1794750381540"`.
- **Not well formed = mismatch.** A claim that is not a JSON string, appears more than once, or is not canonical (sign,
  leading zero, whitespace, decimal point, exponent, non-ASCII digits, more than 19 digits) is `mismatch`.
- **Access tokens only.** The claim is read from the bearer access-token principal. ID tokens and userinfo carry no
  `tenant_gen` (AB#8997); cookie principals, which the middleware already skips, are not checked.

### Outcomes

| Outcome | When | `LogOnly` | `Enforce` |
|---|---|---|---|
| `match` | `tenant_gen` equals the current generation | allowed | allowed |
| `missing` | the tenant has a generation, the token carries no `tenant_gen` (tokens issued before AB#8997) | allowed, logged, counted | refused |
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
[tenant-generation-enforce-plan.md](tenant-generation-enforce-plan.md) (AB#9000).

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
| `grant_type` | only when identity passes one (AB#9017), a fixed set (`TenantGenerationMetrics.GrantTypes`): `token_exchange`, `on_behalf_of`, `refresh_token`, `authorization_code`, `device_code`, `client_credentials`, `password`; anything else `other` |

Never the tenant id, the user, `sub`, `tenant_gen` or a token value. A unit test asserts the exact label set.

The gates, Dash0 queries and log lines for the switch to `Enforce` are in
[tenant-generation-enforce-plan.md](tenant-generation-enforce-plan.md) (AB#9000).

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

Identity (AB#9017) builds a `TenantGenerationCheckRequest` with `Transport = TenantGenerationTransports.Grant` and a
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
