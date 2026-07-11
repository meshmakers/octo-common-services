# Durable Tenant Provisioning & Deprovisioning

## Concept Document & Implementation Plan

**Status:** Draft
**Date:** 2026-07-10
**Work item:** AB#4348 — *Delete+Create of a tenant with the same tenantId leaves a half-provisioned tenant*
**Scope:** Make tenant create/delete a durable, resumable, single-flight lifecycle across `octo-asset-repo-services`, `octo-identity-services`, and `octo-common-services`.

---

## 1. Problem Statement

Provisioning a tenant is a **distributed multi-step process** spanning three services:

```
asset-repo (Create)
  → write RtTenant metadata + create asset DB + import System CK
  → DefaultConfigurationCreatorServiceStandardized.SetupTenantAsync
      → CommandClient: CreateIdentityDataCommandRequest ──► identity service
            → provision identity DB → distribute System.Identity CK → seed roles/groups/clients
```

The process works on the happy path but its **coordination and retry state lives entirely in process memory**. That is the conceptual defect behind AB#4348. Three concrete failure modes were confirmed against the source (2026-07-10):

| # | Confirmed defect | Location |
|---|---|---|
| 1 | **Delete drops the tenant DBs asynchronously, outside the transaction**, and `Create` only does a binary "does the DB exist?" check — it cannot tell a genuine name clash from a DB that is mid-drop. A `Create` shortly after `Delete` fails with `Tenant database 'X' does already exist`, or worse, the in-flight drop later wipes a freshly-created DB. | `TenantContext.DropTenantDatabaseAsync` (`octo-construction-kit-engine-mongodb/.../TenantContext.cs:744`), guard at `TenantContext.cs:497`, `TenantsController.cs:372-405` (see the race comment at `:385-391`) |
| 2 | The saga concurrency guard is a **static in-memory `ConcurrentDictionary` (`TenantsInHandling`)**. On "already in work" the message is **silently dropped** (no retry, no error, no DLQ). It is **per-pod**, so two pods can provision the same tenant in parallel and the second lifecycle message is lost. | `PosCreatePosUpdateTenantConsumer.cs` (`octo-common-services`) |
| 3 | Retry/self-healing state (`_pendingIdentityDataTenantIds`, `FailedTenantRegistry`) is **in-memory only**. `FailedTenantRetryBackgroundService` drains it every 30 s, but on **pod restart the pending set is lost** → a half-provisioned tenant is never driven to completion. | `DefaultConfigurationCreatorServiceStandardized.cs:44`, `FailedTenantRegistry.cs`, `FailedTenantRetryBackgroundService.cs` |

Secondary (error-surface) issue: the `errorCode=13 requires authentication` read against a half-provisioned identity DB is an uncaught `MongoCommandException` and surfaces to the operator as a generic `InternalServerError` (`AdminProvisioningController.cs:228`), while the no-roles case is already handled cleanly as 503 (`:263`).

### Why this is a concept problem, not a bug fix

Each of the three defects is a symptom of the same missing abstraction: **there is no durable, authoritative record of "where is this tenant in its lifecycle, and what still needs to happen?"** Every existing mitigation (two-phase delete, background retry loop, idempotent blueprint seeding, `ProvisionCurrentUser` retry) patches one symptom while the coordinating state stays ephemeral. Adding more in-memory guards or retries cannot fix restart-loss or multi-pod races.

---

## 2. Design Goals

1. **Durable** — lifecycle state survives pod restarts and rescheduling.
2. **Resumable** — a setup that stalled after partial progress is driven to completion automatically, from where it left off.
3. **Single-flight across pods** — at most one worker acts on a given tenant at a time, without an in-memory lock.
4. **Deterministic Delete/Create ordering** — `Create` reacts to an in-progress delete with a clear, actionable signal instead of a timing-dependent error.
5. **Idempotent steps** — every step is safe to re-run; re-running a fully-provisioned tenant is a no-op.
6. **Observable & operable** — operators can see a tenant's lifecycle state and nudge a stuck one without cluster/Mongo access.
7. **Incremental** — ship value in phases; do not require a big-bang rewrite.

---

## 3. What Already Exists (reuse) vs. What Is Missing (build)

Grounded inventory of the current infrastructure:

| Capability | Status | Reuse / Note |
|---|---|---|
| Message bus = **MassTransit + RabbitMQ** | ✅ present | Supports retry, redelivery, DLQ, request/response natively — currently **not wired** for lifecycle. `CreateIdentityDataCommandRequest` already uses `CommandClient` request/response. |
| **Correlation ID** on every tenant op | ✅ present | `PosCreateTenant(TenantId, CorrelationId, Timestamp)`, `TenantDeletionHandle.CorrelationId`. Reuse as the saga key. |
| Pre/Post lifecycle broadcast events | ✅ present | `DistributedTenantNotifications` — natural hooks to create/advance saga records. |
| Consumer-side idempotency (existence checks) | ✅ present | `CreateIdentityDataCommandRequestConsumer` inserts only if absent; blueprint seed + role-restore are idempotent. |
| Background retry loop (PeriodicTimer) | ✅ present | `FailedTenantRetryBackgroundService` — generalize into the reconciler instead of building new infra. |
| **Persistent lifecycle state** | ❌ missing | `RtTenant` has no state enum; no saga collection. **Build.** |
| **Distributed single-flight lease** | ❌ missing | Replace `TenantsInHandling` `ConcurrentDictionary`. **Build.** |
| **Delete tombstone / drop-completion tracking** | ❌ missing | Drop is fire-and-forget with no record. **Build.** |
| Operator "resume/complete setup" command | ❌ missing | **Build** (trivial once state exists). |
| Hangfire | ⚠️ only in `octo-bot-services` | **Not** required; the reconciler is a `BackgroundService` in the service that owns tenant lifecycle (asset-repo, via `octo-common-services`). |

---

## 4. Target Architecture

### 4.1 Core idea — a persisted lifecycle state machine driven by a reconciler

Model tenant provisioning/deprovisioning as an explicit **state machine** whose state is **persisted in the system database**, and drive it forward with a **reconciler loop** (the Kubernetes-controller pattern): the loop periodically (and on lifecycle events) scans for tenants not in a terminal-good state and advances each one, idempotently, until it reaches `Active` or is cleanly `Deleted`.

This single abstraction resolves all three confirmed defects:
- Restart-loss (defect 3) → state is in Mongo, not memory.
- Multi-pod race + silent drop (defect 2) → a Mongo lease gives single-flight; nothing is "dropped", it stays pending until reconciled.
- Delete/Create race (defect 1) → the `Deleting` state is a durable tombstone `Create` can serialize against.

### 4.2 State model

States (per tenant, keyed by `tenantId`):

```
        Create request
             │
             ▼
        ┌──────────┐   step fails / restart      ┌──────────┐
        │ Creating │ ───────────────────────────►│  Failed  │
        └────┬─────┘◄─── reconciler resumes ──────└────┬─────┘
             │ all steps done                          │ operator ReRun / auto-retry
             ▼                                         ▼
        ┌──────────┐                              (back to Creating)
        │  Active  │
        └────┬─────┘
             │ Delete request
             ▼
        ┌──────────┐   drop confirmed
        │ Deleting │ ─────────────────► (record removed → tenant truly gone)
        └──────────┘
```

`Creating` carries a **phase** sub-field so the reconciler resumes at the right step rather than restarting the whole flow:

```
Phase: MetadataWritten
     → AssetDbReady
     → SystemCkImported
     → IdentityDbProvisioned
     → IdentityCkDistributed
     → DefaultConfigSeeded   (→ transition to Active)
```

Persisted fields (per tenant lifecycle record):

| Field | Purpose |
|---|---|
| `TenantId` (unique key) | identity of the tenant |
| `CorrelationId` | ties to existing Pre/Post events; idempotency key |
| `State` | `Creating` \| `Active` \| `Deleting` \| `Failed` |
| `Phase` | current step within `Creating` / `Deleting` |
| `AttemptCount`, `LastError`, `LastTransitionUtc` | observability + backoff |
| `LeaseOwner`, `LeaseUntil` | distributed single-flight (see 4.4) |
| `DatabaseName` | needed to complete an async drop after restart |

### 4.3 Where the state lives — recommendation: a dedicated system-DB collection, **not** a CK field

Two options were considered:

- **Option A — add fields to the CK-modeled `RtTenant`.** Rejected as the default: it requires a **System CK model bump**, which forces a per-tenant migration across every tenant and risks the known dependency-pin skew where dependent CK models keep pinning a stale System version (see the CK-upgrade caveat we already track). Heavy and fragile for what is infrastructure bookkeeping.
- **Option B — a plain infrastructure collection in the system database** (e.g. `tenant_lifecycle`), **outside** the CK model. ✅ **Recommended.** No CK model bump, no cross-tenant CK migration, no version-skew exposure. Precedent exists: `Hangfire.Mongo` and other infra collections are non-CK.

The lifecycle record is bookkeeping about a tenant, not tenant business data — it belongs in an infrastructure collection, not the CK model.

> **Implementation note (Phase 1, 2026-07-11):** `octo-common-services` has **no** raw `IMongoDatabase`
> access — only the `ISystemContext` / `ITenantContext` / `IOctoAdminSession` abstractions. So the store
> **implementation** lives in the engine (`octo-construction-kit-engine-mongodb`, `Runtime.Engine.MongoDb`),
> which resolves the system database's `IMongoDatabase` via `ISystemContext` + `IAdminRepositoryAccess`
> (the same pattern as `IndexUsageService`). The **interface + record + enums** (`ITenantLifecycleStore`,
> `TenantLifecycleRecord`, `TenantLifecycleState`, `TenantLifecyclePhase`) live in `Runtime.Contracts.MongoDb`,
> which `octo-common-services` already references. The Phase 2 reconciler *logic* still lives in
> `octo-common-services` and drives the store through this interface — including the atomic single-flight
> lease, for which the store already carries `LeaseOwner` / `LeaseUntil` and can expose an atomic
> `FindOneAndUpdate` claim method (only the engine can, which is why the store lives there).

### 4.4 Distributed single-flight via a Mongo lease (replaces `TenantsInHandling`)

Replace the static `ConcurrentDictionary` with a conditional Mongo update:

```
claim = update_one(
   filter:  { _id: tenantId, state in {Creating, Deleting, Failed},
              $or: [ leaseUntil == null, leaseUntil < now ] },
   update:  { leaseOwner: podId, leaseUntil: now + leaseTtl },
   returnUpdated: true)
```

Only the pod whose conditional update wins processes the tenant; others skip. A crashed pod's lease expires (`leaseUntil < now`) and the tenant is automatically reclaimed on the next tick — this is exactly the self-healing that the in-memory guard cannot provide. Lease TTL should exceed the longest single step; the worker renews it for long steps.

### 4.5 Delete/Create serialization (fixes defect 1 deterministically)

- **Delete** transitions the lifecycle record to `State = Deleting` (durable tombstone), commits, then performs the async drop. The record is removed **only after the drop is confirmed complete** (the reconciler owns the "finish the drop, then delete the record" step, so a crash mid-drop is resumed, not stranded).
- **Create** consults the lifecycle record first:
  - record in `Deleting` → return **409 "tenant deletion still in progress, retry later"** (actionable, replaces the misleading "database already exist").
  - record in `Creating`/`Failed` → the tenant already exists in-flight; either resume it or reject as duplicate (per operator intent).
  - no record → normal create; write the record in `Creating` **before** touching databases.
- `Create` no longer relies on a binary DB-existence probe for correctness; the authoritative signal is the lifecycle state.

### 4.6 Reconciler loop (generalizes `FailedTenantRetryBackgroundService`)

A `BackgroundService` (hosted where tenant lifecycle is owned) that every N seconds — and opportunistically on Pre/Post lifecycle events — does:

```
for each lifecycle record where state ∈ {Creating, Deleting, Failed} and lease is free:
    try claim lease (4.4); if not won, skip
    switch (state, phase):
       Creating  → run the next not-yet-done step idempotently; advance Phase;
                   when DefaultConfigSeeded → State = Active, drop the record from the work set
       Deleting  → ensure drop completed; then remove the record
       Failed    → apply backoff; if within budget → State = Creating; else leave for operator
    persist State/Phase/AttemptCount/LastError; release/renew lease
```

Because every step is idempotent and keyed by `Phase`, a restart mid-flow simply re-claims and continues. The existing `CreateIdentityDataCommandRequest` request/response and consumer idempotency slot straight into the `IdentityDbProvisioned → DefaultConfigSeeded` steps.

### 4.7 Operator safety valve

Expose `octo-cli` commands (thin wrappers over lifecycle-record edits):
- `CompleteTenantSetup -tid X` / `ReRunTenantSetup -tid X` — set `Failed`/`Active`→`Creating`, clear lease → reconciler drives it to completion.
- `GetTenantLifecycle -tid X` — read state/phase/lastError for support.

### 4.8 Error-surface cleanup (small, ships with Phase 1)

- `Create`: emit **409** for an in-progress delete (4.5).
- `ProvisionCurrentUserInternal`: treat the `errorCode=13` half-provisioned-DB read like `TenantNotReadyException` → clean **4xx/503**, not 500 (`AdminProvisioningController.cs:228`).
- Optional `-w` on `Create`: poll the lifecycle state until `Active` or `Failed` (mirrors `-w` on imports), so scripts like `om_initialize_tenant.ps1` get a definitive result.

---

## 5. Phased Rollout

> **Correction (verified 2026-07-11):** the physical DB drop in `TenantsController.Delete` is
> **awaited before the HTTP response**, and a two-phase delete (commit metadata deletion, then drop)
> already exists (`octo-construction-kit-engine-mongodb` CLAUDE.md → "Race-safe Tenant Delete"). So the
> *sequential* Delete→Create case in defect 1 is already largely mitigated; the remaining exposure is
> concurrent/multi-pod Delete+Create and orphaned databases. Defect 1's durable fix (the `Deleting`
> tombstone, Phase 3) still stands for those.

| Phase | Deliverable | Fixes | Risk |
|---|---|---|---|
| **0 — Quick wins** (independent of state machine) — ✅ **Implemented 2026-07-11** (`dev/4348-tenant-provisioning-phase0`) | `Create` returns **409** (with actionable message) on a tenant/database conflict; `ProvisionCurrentUser` treats `errorCode=13` as transient and returns **503 not 500** (also fixes a retry off-by-one). `Create -w` **deferred to Phase 1** — real readiness needs the durable state. | Error surface + residual/concurrent conflicts of defect 1 | Low, localized |
| **1 — Persistent lifecycle record** | `tenant_lifecycle` collection + write/read on Create/Delete; states `Creating`/`Active`/`Deleting`; backfill existing tenants lazily as `Active`. | Foundation; makes state durable (defect 3) | Low–med |
| **2 — Reconciler + lease** — ✅ **Implemented 2026-07-11** (`dev/4348-tenant-provisioning-phase0`) | Store gains an atomic `TryClaimForReconcileAsync` (Mongo `FindOneAndUpdate` single-flight lease) + `ReleaseLeaseAsync`. A durable reconcile pass runs inside the existing `RetryFailedTenantsAsync` tick (no new hosted service): it claims stalled `Creating` tenants and re-runs `CheckSetupIdentityDataAsync` to completion (→ Active via the Phase 1 hook), marking `Failed` after the attempt budget. Sourced from the **durable store**, so it survives restart and coordinates across pods — unlike the in-memory sets, which stay as a fast-path until Phase 5 removes them. `Deleting` finish stays with Phase 3. | Restart-loss + multi-pod race (defect 3, part of 2) | Med |
| **3 — Delete/Create serialization** | Delete writes `Deleting` tombstone, removed only after confirmed drop; Create serializes against it. | Defect 1 root cause | Med |
| **4 — Operator commands** | `CompleteTenantSetup` / `ReRunTenantSetup` / `GetTenantLifecycle`. | Operability | Low |
| **5 — Cleanup** | Remove `TenantsInHandling`, `_pendingIdentityDataTenantIds`, `FailedTenantRegistry` in favor of the reconciler; wire RabbitMQ DLQ for lifecycle commands. | Debt | Low |

Phase 0 can ship immediately and independently (it maps to the AB#4348 "quick wins"). Phases 1–3 are the concept core.

---

## 6. Migration & Compatibility

- **Existing tenants** have no lifecycle record. On first reconciler pass (or lazily on access), backfill any tenant whose asset + identity DBs are fully provisioned as `State = Active`. Treat "record absent" as "legacy Active" until backfilled, so nothing regresses.
- **No CK model bump** (Option B), so no per-tenant CK migration and no exposure to System-model dependency-pin skew.
- **Rollback:** Phases 1–2 are additive (new collection + generalized loop); the old in-memory path can remain as a fallback until Phase 5 removes it.
- **Multi-service:** the lifecycle record lives in the system DB owned by asset-repo; identity work stays a step invoked via the existing `CommandClient`. No new cross-service datastore.

---

## 7. Design Decisions

Resolved with the team on 2026-07-11:

1. **Reconciler ownership:** ✅ Reconciler logic lives in `octo-common-services` (shared/reusable) and is **hosted as a `BackgroundService` by `octo-asset-repo-services`**, which owns tenant CRUD. Identity/bot may host the same building block later.
2. **Delete while `Creating`:** ✅ **Reject** the delete with a clear message (`tenant is still being created`) rather than cancelling or waiting. Simplest, deterministic semantics; the operator re-issues delete once the tenant is `Active` (or `Failed`). *(Revises the earlier "cancel → Deleting" proposal.)*
3. **Failed budget:** ✅ **10 attempts**, then `Creating → Failed` and wait for an operator (`ReRunTenantSetup`). No infinite auto-retry. Matches the current in-memory value.
4. **RabbitMQ DLQ:** ✅ **Deferred to Phase 5** — build the reconciler first; wire DLQ only if durable state proves insufficient.

Still to be measured (not a design decision):

- **Lease TTL** vs. the longest single step (identity DB provision + CK distribute can be slow) — measure real step durations before fixing the TTL; the worker renews the lease for long steps regardless.

---

## 8. Summary

The reproducible AB#4348 failures are three symptoms of one missing abstraction: **durable, single-flight, resumable lifecycle state**. The fix is not more retries — it is to persist the tenant's lifecycle in the system DB and drive it with a reconciler that leases each tenant, resumes partial work, and serializes Delete against Create. Almost all supporting primitives (MassTransit request/response, correlation IDs, idempotent steps, a periodic background service) already exist; the net-new pieces are a non-CK lifecycle collection, a Mongo lease, and the reconciler that ties them together. Quick wins (Phase 0) can ship independently to relieve the immediate operator pain.
