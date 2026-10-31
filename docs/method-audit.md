# CK method audit (AB#6919, F3.5)

An auditor answers "who called which method on which entity with what outcome" from the event repository: **exactly one
event per invocation id**, for every transport and every outcome. This page describes the record, who writes it and what it
deliberately does not contain.

## Who writes the event

| Writer | Calls that it records | Where |
|--------|-----------------------|-------|
| `CkMethodAuditFilter`, band 100 (`CkMethodInvocationFilterOrder.Audit`), outermost filter of the pipeline | everything that enters the pipeline: success, declared business error, `METHOD_HANDLER_FAILED`, `METHOD_TIMEOUT`, `METHOD_HANDLER_NOT_AVAILABLE`, `METHOD_OUTCOME_UNKNOWN`, a refusal by authorization (band 200, `METHOD_FORBIDDEN`), an answer of the idempotency filter (band 300) including `METHOD_REQUEST_IN_PROGRESS` and a parameters-changed refusal | this repository, `src/Methods/Audit` |
| the gateway (asset-repo, AB#7826) | the calls it refuses **before** the pipeline: forbidden, unknown or invisible target, oversized or invalid parameters; a fault of its own steps (`METHOD_HANDLER_FAILED`, the exception surfaces); a cancellation by the caller before the dispatch (`CALLER_CANCELLED`) | uses `ICkMethodAuditRecorder` directly |
| `CkMethodDispatcher`, last line (AB#7930) | a call that fails in the dispatcher **before the filters** of the pipeline ran: a fault of the resolver or the DI container, an exception that escapes the pipeline before its filters, a cancellation that surfaces before the pipeline | uses `ICkMethodAuditRecorder` when `AddCkMethodAudit()` is registered |

Both go through the public `ICkMethodAuditRecorder` so that the event looks the same wherever it was written:

```csharp
await recorder.RecordAsync(new CkMethodAuditRecord
{
    TenantId = tenant,
    InvocationId = request.InvocationId,
    MethodId = request.MethodId,
    CkTypeId = request.CkTypeId,
    TargetRtId = request.TargetRtId,
    Transport = CkMethodAuditRecord.GatewayTransport,   // "gateway": refused before the pipeline
    Outcome = CkMethodErrorCodes.MethodTargetNotFound,  // an error code, or CkMethodAuditOutcomes.*
    CallerSubject = principal.Subject,
    Duration = stopwatch.Elapsed,
}, ct);
```

`CkMethodAuditRecord.FromResponse(request, response, transport, duration, ...)` and `CkMethodAuditRecord.For(request, outcome,
...)` build the record from a request. The gateway records a call **once**: either it refuses it (recorder, transport `gateway`)
or it hands it to the dispatcher (the filter records it). Register with `services.AddCkMethodAudit()`; the recorder writes to the
registered `IAuditEventSink` (the event log, after `AddNotificationServices()`). `RecordAsync` throws
`CkMethodAuditFailedException` only when the tenant is configured to fail calls on an audit failure (below); the gateway answers
the call as failed in that case.

### One event, one count per invocation id

Whoever observes the end of a call records it, and only that one:

| The call ends ... | Recorded by | Counted in `ck.method.calls` by |
|-------------------|-------------|---------------------------------|
| in the gateway, before the dispatcher (refusal, fault, cancellation in the target-access check) | the gateway, transport `gateway` | the recorder (every `gateway` record) |
| in the dispatcher before the pipeline (resolver or DI fault, cancellation) | the dispatcher, transport `bus` (`in-process` once the in-process transport was chosen), outcome `METHOD_HANDLER_FAILED` or `CALLER_CANCELLED`, no handler instance | the dispatcher, same labels |
| as an exception that escapes the pipeline before its filters ran | the dispatcher, outcome `METHOD_HANDLER_FAILED` | the pipeline (it counts every call it runs) |
| anywhere inside the pipeline | the audit filter | the pipeline |

The dispatcher answers its last-line fault as a normal `METHOD_HANDLER_FAILED` response, so the gateway (which records only
exceptions it catches) sees an answer and does not record it again. A cancellation that reaches the gateway after the dispatch
started is recorded by the filter (or the dispatcher), never by the gateway. The caller of an event written by the dispatcher is
the trusted `ICkMethodPrincipalProvider` principal, falling back to `request.Caller`; the record cannot carry parameters,
results or tokens, and the exception text never reaches it. Without `AddCkMethodAudit()` the dispatcher records nothing but
still counts the call.

## The event

`AuditEvent.Category = "Method.Invocation"` (the auditor's filter). `Metadata` (persisted as the details of the event, System.Notification 2.7.0):

| Key | Content |
|-----|---------|
| `invocationId` | id of this invocation; a replay has its own |
| `methodId`, `ckTypeId`, `targetRtId` | what was called (no target for a static method) |
| `callerSubject`, `callerName` | the trusted principal of the process (`ICkMethodPrincipalProvider`, never the forwarded token), falling back to `request.Caller`; the name is what the caller sent |
| `transport` | `in-process`, `bus` or `gateway` |
| `handlerInstanceId` | the instance that answered; absent when no handler ran |
| `outcome` | `SUCCESS`, `ACCEPTED` (long-running operation), the error code (`METHOD_*` or a declared business error), or `CALLER_CANCELLED` |
| `durationMs` | duration of the call |
| `replayOf` | **first invocation id** when the call was answered from the idempotency store (`CkMethodInvocationFacts.ReplayOf`, set by the idempotency filter); absent otherwise |

Level: `Information` for success and accepted, `Error` for `METHOD_HANDLER_FAILED`, `METHOD_TIMEOUT` and `METHOD_OUTCOME_UNKNOWN`,
`Warning` for everything else (refusals, business errors, cancellation). Text values are cut at 256 characters and control
characters are replaced, so a caller-supplied name cannot forge log lines. The readable message names method, caller, transport,
outcome and duration.

## What it never contains

No parameter (redacted or not), no `resultJson`, no error parameters, no access token, no idempotency key, no parameter
fingerprint, no exception text. The record type has no place for them; the filter sees only the redacted request view of
AB#6914 and reads only ids, the response's error code and the handler instance id. Tests: `CkMethodAuditTests.Canary_*` and the
canary scan of `CkMethodRedactionBusTests` (the event written to the sink is one of the scanned outputs, bus and in-process).

## Replays

A replay is its own invocation with its own invocation id, so it gets its own event. The event carries `replayOf` = id of the
first invocation (never the key or the fingerprint). `METHOD_REQUEST_IN_PROGRESS` and the parameters-changed refusal
(`METHOD_VALIDATION_FAILED`, field `idempotencyKey`) are no replays: they are recorded with their code and without `replayOf`. A keyed call to a method declared `execution.idempotent: true` skips the idempotency store, runs as an unkeyed call and is recorded like one (one event, no `replayOf`).

## Exceptions and cancellation

* An inner filter throws: one event with `METHOD_HANDLER_FAILED`, the exception propagates and the pipeline answers as before.
* The caller cancels (`OperationCanceledException` of the caller's token): one event with `CALLER_CANCELLED`, the exception
  propagates. The method may or may not have run. The event is written with a token that is not cancelled. Where it is
  written depends on where the cancellation surfaces: in the pipeline by the filter, in the dispatcher before the pipeline by the
  dispatcher, in the gateway before the dispatch (target-access check) by the gateway.
* The dispatcher itself fails before the pipeline runs (resolver, DI): the caller gets a `METHOD_HANDLER_FAILED` response and
  one event with the same outcome is written by the dispatcher (see the table above).

## Audit failure

Default: a failing write **never fails the method call**. The recorder logs an `Error` ("Audit event of CK method ... could not
be written", ids and outcome only) and counts `ck.method.audit.failures` (meter `Meshmakers.Octo.Methods`, tag `transport`).
`EventRepositoryAuditEventSink` implements `IStrictAuditEventSink` (`PublishStrictAsync` lets the store's exception escape) so the
failure is visible; a sink that only implements `IAuditEventSink` swallows its own failures and cannot be counted.

For regulated tenants a switch makes the audit failure fail the call (section `Methods:Audit`):

```json
{ "Methods": { "Audit": { "FailCallOnAuditFailure": false, "FailCallOnAuditFailureTenants": [ "regulated-tenant" ] } } }
```

A write is bounded by `WriteTimeout` (default 5 s, `00:00:00` = no limit): a stalled store counts as a failed write, so a call never hangs on the audit. A sink that implements only `IAuditEventSink` cannot report failures; with the switch on the recorder logs a warning once.

`FailCallOnAuditFailure` applies to every tenant, the list (case-insensitive) to the named tenants. Because the event is written
after the call, the method may already have run: the answer is then `METHOD_OUTCOME_UNKNOWN` (check the state before calling
again); a call that was not executed (a refusal) stays the refusal it was.

## Order

Audit (100) is outermost, then authorization (200), then idempotency (300), then the transport. See `CkMethodInvocationFilterOrder`.
