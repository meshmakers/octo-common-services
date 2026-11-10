---
description: Method handler guide: idempotency store contract, keyed parameter fingerprint and the idempotency filter at band 300.
background: true
---

# Method handler: idempotency

Part of the method handler guide; start at [method-handler-sdk.md](method-handler-sdk.md), which lists all parts.

## Idempotency store contract

The filter that uses it (band 300) is described after the fingerprint; this library defines the contract, the lifetime options, the fingerprint and an in-memory store
(`src/Methods/Idempotency/`). The MongoDB store lives in the asset repository (this library has no MongoDB reference, a test pins that).

* **Identity** (`CkMethodIdempotencyKey.Create(tenant, methodId, subject, key)`): tenant + qualified method id + caller **subject** + key.
  The subject is the one of `ICkMethodPrincipalProvider`, never `CkMethodRequest.Caller`. The target is not part of the identity (it is part of the
  parameter fingerprint). `Id` is the SHA-256 (hex) of the four length-prefixed parts and is the only form that is stored; the raw key is not at rest.
  A key has 1 to 128 characters.
* **`ICkMethodIdempotencyStore`**: `ClaimAsync` takes the claim when no live record exists, atomically (of 100 concurrent claims exactly one is
  `Claimed`, the others get the existing record); an expired record counts as absent and is taken over by a conditional replace.
  `CompleteAsync(id, firstInvocationId, Completed | Unknown, response)` stores the outcome; `ReleaseAsync` gives the claim back. Only the owner (the
  invocation that claimed) completes or releases, only while the record is `InProgress`. A record holds state (`InProgress | Completed | Unknown`),
  keyed fingerprint and fingerprint key id, `FirstInvocationId`, method id, `ClaimedAt`, `LeaseUntil`, `ExpiresAt` and the stored envelope.
* **Which outcomes burn the key (OD-4 row 4)**: only a call that was **not executed** releases its claim. An unknown outcome (timeout, outcome unknown,
  an in-flight claim past `LeaseUntil` = deadline + response grace, which `ResolveState` reads as `Unknown`) and `METHOD_HANDLER_FAILED` burn the key:
  the record stays, the caller uses a new key.
* **Fails closed (row 6)**: a store that cannot decide throws (`CkMethodIdempotencyStoreException`), it never reports `Claimed` on a guess.
* **Lifetime** `Methods:Idempotency:Ttl` (`CkMethodIdempotencyOptions`): default 24 h, maximum 7 d (a larger value is clamped and logged once as a warning),
  zero or negative fails the start of the service. Every record carries its own `ExpiresAt` (counted from the first claim; completing does not extend it); readers
  check it themselves, a TTL index is housekeeping only.
* **`METHOD_REQUEST_IN_PROGRESS`**: the platform code for "the first call with this key is still running" (`NotExecuted` for this call; send the same key again
  later, the first result is then replayed). Additive; the contract version does not change.
* Registration: `AddCkMethodIdempotencyOptions()` (options, validation at start, `CkMethodIdempotencyLifetime`) and
  `AddCkMethodInMemoryIdempotencyStore()` (in-memory store unless a store is registered; its state is per process, so tests and single-replica hosts only);
  `AddCkMethodIdempotency()` adds the filter (see below).

## Parameters fingerprint and its key

A repeated idempotency key must belong to the same work. The pipeline therefore fingerprints every call that **carries a key** (a call without
a key computes nothing) from the **raw** request, before the filters get their redacted view: `ckTypeId`, `targetRtId` and the canonical
parameters (members sorted, numbers normalized, array order kept). The fingerprint is an HMAC-SHA-256 with a secret of the environment, so a
database reader or a backup cannot reverse a password from it, and cannot even test a guessed one. It is available to filters as
`context.Facts.ParametersFingerprint` (bytes and key id, for the store) and is never logged, audited or serialized.
A filter verifies a stored fingerprint with `context.Facts.MatchesStoredFingerprint(stored)`: it accepts the current key, the previous key
(rotation) and the unkeyed SHA-256, and is constant-time.

**Configuration** (section `Methods:Idempotency:Fingerprint`, read at the start of the service, the same on every replica):

| Key | Meaning |
|---|---|
| `Current:Id` | Public name of the key, stored with each fingerprint (`A-Z a-z 0-9 . _ -`, at most 64). |
| `Current:Secret` | Base64 of at least 32 random bytes (`openssl rand -base64 32`). A secret, never in a checked-in file. |
| `Previous:Id`, `Previous:Secret` | The key of the rotation before; only verifies stored fingerprints. Needs `Current`; its id differs. |

As environment variables: `OCTO_Methods__Idempotency__Fingerprint__Current__Id` and `...__Secret`, `...__Previous__...`.
A malformed or half-set key fails the start of the service; the message names the setting, never the value.

**Rotation:** set the new key as `Current` (new id), move the old one to `Previous`, roll all replicas; remove `Previous` after the longest
idempotency TTL (7 days at most).

**No key configured (fail closed):** a call with an idempotency key to a method that has sensitive parameters, or to an unknown method, is refused
before the idempotency filter and the handler run: `METHOD_VALIDATION_FAILED`, `field` `idempotencyKey`, `reason` `fingerprint-key-missing`. A method
without sensitive parameters uses a plain SHA-256 (key id `sha256`). Authorization still comes first, so an unauthorized caller learns nothing.

**Ops prerequisite (Platform Ops):** before keyed calls are enabled on a shared environment (test-2, staging-1, prod-1, prod-2), the
environment needs the fingerprint secret as a Vault entry and a Helm secret value, shared by all asset-repository replicas, with the
slot for the previous key. Until then keyed calls to methods with sensitive parameters are refused there.

## Idempotency filter at band 300

`AddCkMethodIdempotency()` adds `CkMethodIdempotencyFilter` (band 300, innermost before the transport). The host also registers a store
(`AddCkMethodInMemoryIdempotencyStore()` for tests and single-replica hosts; without any store the pipeline cannot be built and the host fails instead of
running keyed calls unprotected) and the authorization (`AddCkMethodAuthorization()`), which supplies the principal provider. A call **without a key**
passes straight through: no store access, no principal lookup.

**A method that declares `execution.idempotent: true` ignores the key.** The method itself is safe to repeat, so a keyed call to it never touches the
store: the key is accepted and ignored (no format or length check, no scope, no caller subject needed, no fingerprint, no `result-sensitive` refusal, no
`fingerprint-key-missing` refusal), and the call runs as an unkeyed one, every time. Two calls with the same key and the same or different parameters both
execute; nothing is stored, nothing is replayed (`Facts.ReplayOf` stays empty). The flag is read from the method definition in the CK cache
(`ICkMethodSensitivityResolver.IsIdempotent`, same source as authorization); an unknown method, an absent or `false` flag or a failing source count as
not idempotent, so the store stays in charge. Audit and metrics are unchanged: still exactly one audit event and one `ck.method.calls` increment per call
(a replay marker never appears, because nothing is replayed). The flag does not change the duplicate-delivery refusal in [Duplicate deliveries and failing sends](method-handler-sdk-delivery.md#duplicate-deliveries-and-failing-sends): that still answers
`METHOD_OUTCOME_UNKNOWN` for a possible duplicate delivery.

For a call with `IdempotencyKey` to any other method the filter decides in this order (every refusal is a normal failed envelope, the handler does not run, nothing but
the claim is stored):

| Situation | Answer |
|---|---|
| The method's result is sensitive, or the method is unknown (OD-4 G2) | `METHOD_VALIDATION_FAILED`, `field` `idempotencyKey`, `reason` `result-sensitive`. Nothing is stored; call without a key. |
| Neither a `sub` nor a `client_id` on the principal from `ICkMethodPrincipalProvider`, or a `sub` that starts with `client:` | same, `reason` `caller-unknown` (a key has no scope without a caller; `CkMethodRequest.Caller` is never used). |
| Key longer than 128 characters | same, `reason` `key-too-long`. |
| No parameter fingerprint on the invocation (idempotency options not registered) | same, `reason` `fingerprint-unavailable` (fail closed). |
| Store cannot decide (throws) | `METHOD_HANDLER_NOT_AVAILABLE`, `reason` `idempotency-store-unavailable`; not executed, retry later. |
| This call took the claim | executes, then stores or releases (below). |
| Existing record, other parameters, or a stored fingerprint that cannot be verified (empty, or made with a key id that is no longer configured) | `METHOD_VALIDATION_FAILED`, `reason` `parameters-changed`. Checked before the state. |
| Existing record, first call still running (until request deadline + `Methods:Dispatcher:ResponseGrace`) | `METHOD_REQUEST_IN_PROGRESS`. |
| Existing record, outcome unknown (stored `Unknown`, or the lease lapsed) | `METHOD_OUTCOME_UNKNOWN`: neither executed nor answered; the key is burned, use a new key. |
| Existing record, completed | the stored envelope, with the `invocationId` of **this** call; `Facts.ReplayOf` = invocation id of the first call. |

**Scope and position:** tenant + qualified method id + caller scope + key, stored as a hash. The caller scope comes from the principal provider
(`CkMethodIdempotencyKey.ScopeOf`): a user is scoped by its `sub`; a client-credentials caller (no `sub`) by `client:<client_id>`, so a
user id and a client id never share a scope, even when they are the same string; a user whose `sub` itself starts with `client:` has no
scope (`caller-unknown`), so the two namespaces stay disjoint (platform subs are object ids and never do). Only the idempotency scope carries the prefix: the audit
`callerSubject` of a service caller stays the plain client id. `CkMethodPrincipal.FromClaimsPrincipal` maps `client_id` only for a token without
`sub`, so a service caller (pipeline, bot, CLI with client credentials) can send a key through the gateway. The
filter sits inside authorization, so an unauthorized caller never learns whether a key exists, and behind the fingerprint-key refusal of the pipeline.
The audit filter (100) sees every outcome and reads `Facts.ReplayOf` after `next()`.

**What happens after the first call ran (OD-4 row 4):**

| Outcome of the first call | Record |
|---|---|
| success, accepted (async), a code the method declares | stored (`Completed`), replayed |
| `METHOD_HANDLER_FAILED` | stored (`Completed`) and replayed as that failure: the key is burned, the caller needs a new key to try again |
| not-executed platform code (`METHOD_HANDLER_NOT_AVAILABLE`, `METHOD_FORBIDDEN`, `METHOD_VALIDATION_FAILED`, ...: `CkMethodErrorCodes.Catalog`, `NotExecuted`) | **released**: the same key may run again at once |
| `METHOD_TIMEOUT`, `METHOD_OUTCOME_UNKNOWN`, a platform code this version does not know, an exception, cancellation by the caller | `Unknown`: burned |

If the store fails *after* the method ran, the caller still gets the answer; the record stays in progress and reads as `Unknown` once its lease lapsed (burned,
never reusable by guess). The log names method, invocation id and the opaque key id (the hash), never the raw key, a fingerprint, a parameter or a result.
