# Switching the tenant generation check to `Enforce` per environment (AB#9000)

Security item AB#8970. This is the operator plan for the step after AB#8999 (this repository, check in `LogOnly`) and
AB#9017 (octo-identity-services #305, identity refuses old grants and runs the tenant check on its own API). The check
itself is described in [tenant-generation-check.md](tenant-generation-check.md).

The plan covers **two switches** per environment. Both are gated by the same conditions and are usually done in the same
run, but they are separate values and can be rolled back separately:

| Switch | What it does in `Enforce` | Today |
|---|---|---|
| **S1: identity tenant authorization** — `TenantAuthorization:UserTokenEnforcement` and `TenantAuthorization:ServiceTokenEnforcement` on **identity only** | identity's own tenant API answers 403 to a user or service token issued for another tenant (new for identity with #305; the other services enforce this already) | code default `Enforce`; the octo-mesh chart renders `LogOnly` unless the cluster values say otherwise |
| **S2: tenant generation check** — `TenantAuthorization:TenantGenerationEnforcement` on **every service that runs the check** (identity included) | tokens and grants of an earlier tenant with the same id, or without `tenant_gen`, are refused (403 / `METHOD_FORBIDDEN` / `invalid_grant`); a failed lookup is 503 / `METHOD_HANDLER_FAILED` / `server_error` | code default `LogOnly`; **no chart can set it yet** (follow-up F1) |

Nothing in this document changes an environment. Every switch is an environment write: Odysseus (`dev-ops`) executes
it per cluster with Gerald's go for that cluster in his own words; staging-1, prod-1 and prod-2 never without it.

## Order

`test-2` → `staging-1` → `prod-1` → `prod-2`. An environment is switched only after the previous one has run `Enforce`
for at least 24 h without an unexpected refusal (step 6 below). The approval of one cluster does not carry over to the
next.

Preconditions on every environment:

- **P1** identity with AB#9017 (#305) runs there (the release that carries it reached the cluster; record the date).
- **P2** for S2 only: the cluster values can set `TenantGenerationEnforcement` on every service (follow-up F1).

## Gates (decided by Gerald, 2026-11-15)

An environment is switched only when **both** hold, written down in the gate check comment (step 1 below):

**(a) Time.** The longest refresh-token lifetime of the environment (or the longest access-token lifetime, if larger,
see below) has passed since identity with #305 reached it.
Refresh tokens, authorization codes and device codes issued before #305 carry no stored generation (`missing`); in
`LogOnly` a refresh stores the current generation on the rotated refresh token, so active sessions migrate by
themselves, and an unused old refresh token expires at the latest one lifetime after #305 arrived. Accepted residual
risk (Gerald, 2026-11-15): in `LogOnly` such a grant takes the current generation on refresh.

- The lifetime that counts is the client setting `AbsoluteRefreshTokenLifetime` (seconds; identity maps it to the
  OpenIddict refresh-token lifetime; `SlidingRefreshTokenLifetime` is not used). Default `2592000` (30 days). Read the
  largest value of any client on the environment (octo-cli `GetClients` per tenant, or Studio) and record it. A client
  with `0` or no value gets the OpenIddict server default (14 days; identity configures no other); it does not raise the
  maximum above the 30-day default.
- Read the largest `AccessTokenLifetime` (default `3600`) the same way. Identity does not bound it by the refresh-token
  lifetime, so the wait is the **larger** of the two maxima: an access token issued before the `tenant_gen` claim
  (AB#8997) stays valid for its own lifetime.
- **Alternative** (AC1 of AB#9000): instead of waiting, the gate check states that users with older refresh tokens must
  sign in again on this environment. One of the two, per environment, written down.

**(b) No legitimate "would be denied".** Over the observation window before the gate check (proposal: the last 7 days;
the window is Gerald's call) the logs and the metric show no refusal `Enforce` would make for a legitimate caller:

1. metric `missing` is zero for `transport` `http` and `method`, and for `transport=grant` zero or every client
   explained (old refresh tokens of the residual risk above);
2. every `mismatch` is explained (a token of a deleted and recreated tenant is exactly what `Enforce` must refuse);
3. `lookup_failed` is zero or explained (in `Enforce` it becomes a 503 / `METHOD_HANDLER_FAILED` / `server_error`);
4. identity logs no "would be denied" line of its tenant authorization (S1) for a legitimate caller; each client that
   appears is either fixed (e.g. a client-credentials login without `acr_values=tenant:<id>`) or added to
   `TenantAuthorization:CrossTenantServiceClientIds` by its own change, before the switch.

## What to watch

### Log lines (exact text, from the code)

All are `Warning`. The S2 lines name client, tenant, outcome and transport; the S1 lines name subject, client and both
tenants. None names the token or the `tenant_gen` value. The S2 lines are rate-limited to one per minute per transport,
client, outcome and decision. Only `missing` and `mismatch` are logged: `lookup_failed` is counted but never logged, so
gate (b)3 is checked through the metric only.

| Source | `LogOnly` line (fixed part to search for) | Line after the switch |
|---|---|---|
| S2, every service and identity's grants (`TenantGenerationCheck`) | `Access token of client '…' for tenant '…' failed the tenant generation check (…, transport …). This would be denied with TenantGenerationEnforcement=Enforce` | `Denied: access token of client '…' for tenant '…' failed the tenant generation check` |
| S1, identity user tokens (`TenantAuthorizationMiddleware`) | `User token of subject '…' (client '…') was issued for tenant '…' but addresses tenant '…'. This would be denied with UserTokenEnforcement=Enforce` | `Denied: user token of subject '…' (client '…') was issued for tenant '…' but addresses tenant '…'` |
| S1, identity service tokens without `tenant_id` | `Service token of client '…' carries no tenant_id claim and addresses tenant '…'. This would be denied with ServiceTokenEnforcement=Enforce` | `Denied: service token of client '…' carries no tenant_id claim` |
| S1, identity service tokens of another tenant | `Service token of client '…' was issued for tenant '…' but addresses tenant '…'. This would be denied with ServiceTokenEnforcement=Enforce` | `Denied: service token of client '…' was issued for tenant '…'` |
| identity, grant issued without generation (lifecycle read failed) | `Grant for tenant '…' stored without a tenant generation: the generation could not be read (…). It counts as missing when it is redeemed` | same |

The S2 line also comes from identity's grants: there it reads "access token" too, with `transport grant`.
The S1 lines are logged by every service that runs `TenantAuthorizationMiddleware` in `LogOnly`; for S1 filter on the
identity container (`identity` in the octo-mesh chart). Asset repository and communication controller already enforce
user tokens on every cluster.

### Dash0 queries

The Dash0 dataset is named after the cluster. Logs with D0QL (`sql`), time range = the observation window:

```sql
-- S2: "would be denied" by container (all services)
SELECT resource_attributes['k8s.container.name'] AS container, count() AS lines
FROM logs
WHERE body LIKE '%This would be denied with TenantGenerationEnforcement=Enforce%'
GROUP BY container ORDER BY lines DESC

-- S1: identity's tenant authorization
SELECT body, count() AS lines
FROM logs
WHERE resource_attributes['k8s.container.name'] = 'identity'
  AND (body LIKE '%This would be denied with UserTokenEnforcement=Enforce%'
       OR body LIKE '%This would be denied with ServiceTokenEnforcement=Enforce%')
GROUP BY body ORDER BY lines DESC LIMIT 50
```

Then read the matching lines (`getLogRecords`, body filter as above) to list the clients. After a switch, search for the
`Denied: …` texts of the table the same way.

Metric (PromQL) on `octo.tenant_auth.generation_checks` (meter `Meshmakers.Octo.TenantAuthorization`; labels
`outcome`, `mode`, `transport`, `client_id`, `grant_type`):

```promql
# (b)1 + (b)2 + (b)3: everything that is not a match, per service, client and path.
# Run the first two as range queries over the whole observation window with step 1d; an instant query shows only the last day.
sum by (service_name, outcome, transport, grant_type, client_id) (
  increase({otel_metric_name="octo.tenant_auth.generation_checks", outcome=~"missing|mismatch|lookup_failed"}[1d])
)

# (a) old refresh tokens still in use (expected to fall to zero within the refresh-token lifetime)
sum by (client_id) (
  increase({otel_metric_name="octo.tenant_auth.generation_checks", transport="grant", grant_type="refresh_token", outcome="missing"}[1d])
)

# after the switch: the mode label shows which services enforce
sum by (service_name, mode) (increase({otel_metric_name="octo.tenant_auth.generation_checks"}[1h]))
```

The PromQL selectors were written without Dash0 access; check the metric and label spelling on the first gate check
(test-2) and correct this page if Dash0 names them differently. The metric never carries tenant, user, `sub`,
`tenant_gen` or token values, so the per-tenant view is only in the log lines.

## Exact values

### S1: identity tenant authorization (chart values exist, octo-helm-core #40)

In `octo-mesh-deployment`, `clusters/<cluster>/values-octo-mesh.yaml`:

```yaml
services:
  identity:
    userTokenEnforcement: Enforce      # LogOnly | Enforce   -> OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT
    serviceTokenEnforcement: Enforce   # LogOnly | Enforce | Disabled -> OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT
```

Without these keys the chart renders `LogOnly` for identity. Any other value fails the render.

### S2: tenant generation check (no chart value yet)

Configuration key `TenantAuthorization:TenantGenerationEnforcement`, environment variable
`OCTO_TENANTAUTHORIZATION__TENANTGENERATIONENFORCEMENT` (case-insensitive), values `Disabled` | `LogOnly` | `Enforce`.
It must be set on every service of the environment that runs the check — the hosts of `UseOctoTenantAuthorization()` and
of the CK method registrations:

| Service | Chart |
|---|---|
| identity, asset repository, bot, communication controller, AI services | octo-mesh (octo-helm-core) |
| report services | octo-mesh-reporting (octo-helm-pro) |
| MCP service | octo-mesh-mcp (octo-helm-pro) |
| AI services, where installed with that chart | octo-mesh-ai (octo-helm-pro) |

Platform services runs no tenant gate (AB#5051) and needs no value. Re-check the list with
`git grep -n -E "UseOctoTenantAuthorization\(|AddCkMethodHandlers\(|AddCkInProcessMethodHandlers\(|AddCkMethodCallerTokenValidation\("`
over the service repositories before F1.

Today none of these charts renders the variable and none has a generic `extraEnv` for these services, so every service
runs the code default `LogOnly`. Follow-up **F1** adds one value per chart (e.g. `tenantAuthorization.tenantGenerationEnforcement`,
chart default: not rendered, so the code default applies; validated like the existing modes) that renders the variable on
every service of the chart. Until F1 is released and pinned, S2 cannot be switched on any cluster; `kubectl set env` is
not a substitute (it is undone by the next CD run).

Switch all services of a cluster in one CD run. A partial switch is not unsafe (the other services keep logging), but the
protection is only complete when every service enforces.

## Switch, per environment

1. **Gate check** recorded as an `[Odysseus]` or `[Ariadne]` comment on AB#8970: P1 date, longest
   `AbsoluteRefreshTokenLifetime` and `AccessTokenLifetime`, the date (a) is met (or the forced re-login statement), the query results of (b)
   with time range, every client that still produces `missing`, `mismatch`, `lookup_failed` or an S1 "would be denied"
   line, with its explanation.
2. **Gerald's go** for this cluster, in his own words.
3. **Change** (Odysseus): PR in `octo-mesh-deployment` with the S1 values and, once F1 is there, the S2 value, plus the
   pre-write impact list (`helm diff`: only these environment variables change).
4. **Roll out** with the cluster's octo-mesh CD run (and the reporting, MCP and AI CD runs for S2).
5. **Smoke test** right after the rollout: fresh interactive sign-in (identity UI and Refinery Studio), a GraphQL query,
   a CK method call, a token refresh (Studio session older than one access-token lifetime), octo-cli `AuthStatus` and a
   client-credentials call with `acr_values=tenant:<id>`.
6. **24 h watch**: the metric shows no unexpected `mismatch`, `missing` or `lookup_failed`, and the `Denied: …` lines
   name no legitimate caller. Record the result on AB#8970. Then the next environment may start.

## Rollback

Set the value back to `LogOnly` in the cluster values and run the same CD again; the pods roll as in a normal rollout,
no data changes and no restart beyond it. Requests refused under `Enforce` work again at once; users who signed in again
in the meantime are not affected. `Disabled` exists for `TenantGenerationEnforcement` and identity's
`ServiceTokenEnforcement` (not for `UserTokenEnforcement`), but hides the inventory (no log, no metric) — use it only as an
emergency exit and go back to `LogOnly` afterwards.

## Last step: code default

Once all four environments have run `Enforce` for one release train, the code default of `TenantGenerationEnforcement`
flips to `Enforce` in this repository (separate small PR, CHANGELOG entry; AB#9000 AC4). The S2 value of F1 stays in the
cluster values until **every** service of that cluster runs a release built with the new default (each consumer picks it
up only with its next package update and deployment); removing it earlier turns that service back to `LogOnly`. The
explicit S1 identity values stay as long as the chart renders `LogOnly` for missing keys; they can only go once the chart
default for identity is `Enforce`.

## Per-cluster checklist

Dates of the switches are decided by Gerald; the empty cells are filled at the gate check and the switch.

| Cluster | P1: identity #305 since | Longest refresh- / access-token lifetime | (a) met on / re-login statement | (b) gate check comment (AB#8970) | Gerald's go | S1 identity `Enforce` | S2 tenant_gen `Enforce` (needs F1) | 24 h watch recorded |
|---|---|---|---|---|---|---|---|---|
| test-2 | 2026-11-15 (rolling CD of `main`) | | | | | set by octo-mesh-deployment #834 (2026-11-15) | | |
| staging-1 | (release with #305; identity renders `LogOnly`) | | | | decided by Gerald | | | |
| prod-1 | (release with #305; identity renders `LogOnly`) | | | | decided by Gerald | | | |
| prod-2 | (release with #305; identity renders `LogOnly`) | | | | decided by Gerald | | | |

test-2 runs S1 in `Enforce` since #305 arrived there (intended, octo-mesh-deployment #834); its gate check for S1 is
therefore a review of the `Denied: …` lines on identity, not a precondition.

## Follow-up work

- **F1** chart value for `TenantGenerationEnforcement` in octo-mesh (octo-helm-core) and in octo-mesh-reporting,
  octo-mesh-mcp and octo-mesh-ai (octo-helm-pro), then the chart pins in `octo-mesh-deployment`. Blocks S2 on every cluster.
- **F2** (optional) a Dash0 check rule on `octo.tenant_auth.generation_checks` with `outcome` `mismatch` or
  `lookup_failed` in `mode=enforce`, so a refusal wave after a switch alerts instead of waiting for the 24 h review.
- **F3** the code default flip of the last step.
