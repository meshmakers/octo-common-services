---
description: Method handler guide: asynchronous handlers, the accepted outcome, the durable operation record, store contract, options and metric.
background: true
---

# Method handler: asynchronous handlers and the operation record

Part of the method handler guide; start at [method-handler-sdk.md](method-handler-sdk.md), which lists all parts.

## Asynchronous handlers: long-running operations (F3.6)

A method declared `execution.mode: async` (CK v2) does not return its result to the caller. The handler accepts the
call, the platform records a durable **operation** and answers with an operation handle; the caller reads state, progress and
result later through the handle (GraphQL `runtime.operations`). Decision OD-5 (`methodConsistency.md`): at most
once after accept, the record is written before the accepted envelope, a lost handler ends as `METHOD_OUTCOME_UNKNOWN`, 7 days
retention after completion, no cancel, a repeated idempotency key returns the same operation id.

### The handler

Validate the parameters synchronously and answer a declared error at once when they are wrong. Return `Accepted` only when the work
can start:

```csharp
public sealed class BackupDumpHandler(IDumpService dumps)
    : ICkMethodHandler<BackupDumpParameters, BackupDumpResult>
{
    public Task<CkMethodOutcome<BackupDumpResult>> HandleAsync(
        CkMethodContext<BackupDumpParameters> context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.Parameters.Target))
        {
            return Task.FromResult(CkMethodOutcome<BackupDumpResult>.ValidationFailed("target", "required"));
        }

        var tenantId = context.TenantId; // copy what the work needs; never keep the token
        return Task.FromResult(CkMethodOutcome<BackupDumpResult>.Accepted(async (progress, ct) =>
        {
            await progress.ReportAsync(CkMethodOperationProgress.Create(10, "COPYING_COLLECTIONS"), ct);
            var file = await dumps.DumpAsync(tenantId, ct);
            return new BackupDumpResult { Value = file };          // or CkMethodOutcome<...>.Fail("DUMP_FAILED", ...)
        }));
    }
}
```

`CkMethodOutcome.Accepted(work)` is the same for a method without a result. Rules:

* The work runs **once**, after the answer has left, in a detached flow (no ambient state of the caller), in the handler's DI scope,
  which stays open until the work ends. Its token is cancelled when the host stops; the operation then ends as
  `METHOD_OUTCOME_UNKNOWN` (`reason` `handler-lost`). There is no resume and no retry.
* The work answers success (result) or a declared error. An undeclared code is `METHOD_HANDLER_FAILED`, an exception is
  `METHOD_HANDLER_FAILED` with `correlationId` (logged, secrets masked), an unacknowledged write is `METHOD_OUTCOME_UNKNOWN` - the same
  mapping as a synchronous call. The work cannot accept again.
* `ICkMethodOperationProgress.ReportAsync(progress)`: an optional percentage (0-100), an optional language-neutral stage code
  (UPPER_SNAKE_CASE) and up to 16 string parameters; no message text. A report is also a heartbeat. A store problem while reporting is
  logged and never fails the work.
* `Accepted` for a method that is **not** declared `execution.mode: async` is refused with `METHOD_HANDLER_FAILED` (handler bug), and
  nothing is recorded.

### Registration

```csharp
services.AddCkMethodHandlers("BotServices", m => m.Handle<BackupDumpHandler>("System/Tenant.Dump-1"));
services.AddCkMethodMongoOperationStore();   // package Meshmakers.Octo.Services.Methods.MongoDb, system database
services.AddCkMethodOperations();            // runner, heartbeat, sweep
```

Without `AddCkMethodOperations()` a handler that returns `Accepted` is answered `METHOD_HANDLER_FAILED`. A host that only reads
operations (the gateway) registers the store and `AddCkMethodOperationSweep()`. Tests and single-replica hosts may use
`AddCkMethodInMemoryOperationStore()` (not durable, not shared).

### What happens on a call

1. The handler returns `Accepted(work)`.
2. The host writes the record (`Accepted`, owner, method, invocation id, handler instance) **before** it answers. If the write fails,
   the caller gets `METHOD_HANDLER_NOT_AVAILABLE` (`reason` `operation-store-unavailable`) and the work never starts. A host that
   is shutting down records nothing and answers `METHOD_HANDLER_NOT_AVAILABLE` (`reason` `host-stopping`), so the caller can retry.
3. The caller gets `success: true` with `operation { operationId, acceptedAtUtc, retryAfterSeconds }`; `expiresAtUtc` is not known
   yet (it is set on completion) and stays empty.
4. The work starts: `Running`, progress, heartbeats every `HeartbeatInterval`.
5. The work ends: `Succeeded` with the result JSON (none for a result the CK model marks `sensitive`) or `Failed` with code and
   parameters; `expiresAtUtc` = completion + `Retention`. A final record never changes again.
6. A handler that dies (killed, crashed): no heartbeat for `StaleAfter`, the sweep of any replica ends the record `Failed` /
   `METHOD_OUTCOME_UNKNOWN` / `handler-lost`. A late outcome of that work is dropped. Nothing restarts it.

### The record and the store

`CkMethodOperationRecord`: tenant (lower-case), operation id (32 hex chars, opaque), method id, invocation id, correlation id,
owner (`sub` of a user, else `client_id` of a service caller - from the trusted principal, never from `CkMethodRequest.Caller`),
handler instance, state, progress, result JSON, error, accepted / started / heartbeat / completed / expires times. It **never**
contains the parameters, the caller token (OD-10: the principal stays in the process) or a sensitive result.

`ICkMethodOperationStore` (contract tests `OperationStoreContract`, run against the in-memory and the MongoDB store):

| Member | Use |
|--------|-----|
| `CreateAsync(record)` | before the accepted envelope; duplicate id in the tenant or a database problem throws `CkMethodOperationStoreException` |
| `GetAsync(tenant, operationId)` | read one; `null` for unknown, expired or another tenant's id (the gateway maps all three to `OPERATION_NOT_FOUND`) |
| `ListAsync(query)` | paged list of one tenant, newest first; filters `Owner` (subject, or client id without subject), `MethodId`, `State`, `SinceUtc`; `Skip`, `Take` (1-200, default 50); returns `TotalCount` |
| `MarkRunningAsync` / `ReportProgressAsync` / `HeartbeatAsync` | open records only |
| `CompleteAsync` | first final state wins |
| `FailLostAsync(staleBefore, now, retention)` | the sweep, across tenants (the in-memory store also drops expired records here; MongoDB uses the TTL index) |

Every read names the tenant (an empty tenant is an `ArgumentException`); a read for tenant B never returns a record of tenant A.
The MongoDB store (`CkMethodMongoOperationStore`) keeps one document per operation in the collection `MethodOperations` of the system
database, with a unique index `tenantId + operationId`, a TTL index on `expiresAt` (open records have none) and indexes for the list and
the sweep. It writes with its own admin client (majority read and write concern, 30 s `wtimeout`, OD-7), outside any request transaction.

### Settings (`Methods:Operations`, env `OCTO_Methods__Operations__*`)

| Setting | Default | Bounds |
|---------|---------|--------|
| `Retention` | 7 days | 1 hour to 30 days |
| `HeartbeatInterval` | 15 s | at least 1 s |
| `StaleAfter` | 2 min | at least 3 x `HeartbeatInterval` |
| `SweepInterval` | 1 min | at least 1 s |
| `RetryAfterSeconds` | 5 | 1 to 3600 |

An invalid value stops the service at start; the message names the setting and its bounds, never the configured value.

### Idempotency

A keyed call to an async method behaves like any keyed call ([idempotency filter](method-handler-sdk-idempotency.md#idempotency-filter-at-band-300)): the first call stores the accepted
envelope, a repeat with the same key and parameters gets the same envelope (same `operationId`) with its own `invocationId`, and the
handler is not called again, so only one operation exists. Test: `tests/Methods.Tests/Operations/AsyncIdempotencyTests.cs`.

### Metrics

`ck.method.operations` (counter, meter `Meshmakers.Octo.Methods`), labels `method_id` and `state` (`accepted`, `running`, `succeeded`,
`failed`); operations the sweep ends count as `failed` with `method_id` `other`. No tenant or user label. See `docs/method-metrics.md`.

### Tests

`tests/Methods.Tests/Operations/` (record written before the envelope, progress and final states, declared / undeclared / exception
outcomes, sensitive result, redaction, sync method refused, host without the runtime refused, store down = not accepted, host stop,
lost handler and sweep, heartbeat, options, metrics, idempotency) and `tests/Methods.IntegrationTests/Operations/` (contract against a
MongoDB replica set, indexes, readable after a restart, a handler host killed while working ended by the sweep of another host, raw
documents without parameters or token).
