# Implementing a method handler (bus transport)

Package `Meshmakers.Octo.Services.Methods`. Owner: AB#6875 (F3.1). Wire format: `method-wire-contract.md`.

A CK method is declared in a construction kit model. The service that owns the model implements it with a **typed
handler**. You write the handler class and one line of registration; queues, JSON, announcements and heartbeats belong to
the host.

## 1. What the model generates

For a method `ChangePassword-1` of type `Account` the CK source generator emits (see the CK language 2 guide, "Generated
Code"): `AccountChangePasswordParameters` (required parameters are `required` properties), `AccountChangePasswordResult { Value }`
when the method has a result, and the method id constant in the model's `*CkIds` class, for example
`Sample.Accounts/Account.ChangePassword-1`. Use the constant; never type the id by hand.

## 2. The handler

```csharp
public sealed class ChangePasswordHandler(IAccountStore accounts)
    : ICkMethodHandler<AccountChangePasswordParameters, AccountChangePasswordResult>
{
    public async Task<CkMethodOutcome<AccountChangePasswordResult>> HandleAsync(
        CkMethodContext<AccountChangePasswordParameters> context, CancellationToken cancellationToken)
    {
        if (context.Parameters.NewPassword.Length < 8)
        {
            // a code the method declares in its CK model (errors: - code: PASSWORD_TOO_SHORT)
            return CkMethodOutcome<AccountChangePasswordResult>.Fail("PASSWORD_TOO_SHORT", ("minLength", "8"));
        }

        var length = await accounts.SetPasswordAsync(context.TargetRtId!, context.Parameters.NewPassword,
            cancellationToken);
        return length is null
            ? CkMethodOutcome<AccountChangePasswordResult>.TargetNotFound()        // METHOD_TARGET_NOT_FOUND
            : new AccountChangePasswordResult { Value = length };                  // success
    }
}
```

The same code runs in the integration test `tests/Methods.IntegrationTests/CkMethodHandlerBusTests.cs` against a real
RabbitMQ.

* A method without a result implements `ICkMethodHandler<TParameters>` and returns `CkMethodOutcome.Ok()`.
* The handler is created **per invocation in a new DI scope**: take scoped services (repositories, units of work) in the
  constructor. Register nothing yourself, `Handle<THandler>` does `AddScoped<THandler>()`.
* `context` carries the caller (informational), `TenantId`, `CkTypeId`, `TargetRtId` (null for static methods),
  `CorrelationId`, `IdempotencyKey`, `DeadlineUtc`/`RemainingTime` and `CallerAccessToken`. It has **no database session and
  no transaction**: a method runs outside the request transaction of the caller, open your own unit of work.
* **Authorization**: the CK model declares who may call; the handler may re-check with `CallerAccessToken`. Do not trust
  `Caller` (it is not authenticated on the bus).
* **Cancellation and deadline**: `cancellationToken` is cancelled at `DeadlineUtc`. Pass it to every awaited call. A handler that
  ignores it and finishes late still answers late; the caller has then already received `METHOD_TIMEOUT`.

## 3. Errors

The wire carries a stable **code and parameters, never message text**; clients render their own text.

| You do | The caller gets |
|---|---|
| `return CkMethodOutcome<T>.Fail("MY_CODE", ("name", "value"))` or `throw new CkMethodException("MY_CODE", ...)` | `error.code = MY_CODE`, `error.parameters` |
| `ValidationFailed(field, reason)`, `TargetNotFound()`, `Forbidden(reason)` | `METHOD_VALIDATION_FAILED` / `METHOD_TARGET_NOT_FOUND` / `METHOD_FORBIDDEN` with `methodId` |
| any other exception | `METHOD_HANDLER_FAILED` with `correlationId`; the exception is logged with that id |
| a code the method does not declare | `METHOD_HANDLER_FAILED` (a handler bug, logged) |

Parameter values of an error never contain method parameter values (passwords, tokens). Codes: UPPER_SNAKE_CASE, 3 to 64
characters, no `METHOD_` prefix (reserved for the platform). Before the handler runs the host answers, without calling it:
`METHOD_CONTRACT_VERSION_UNSUPPORTED`, `METHOD_HANDLER_NOT_AVAILABLE` (method not registered), `METHOD_TIMEOUT` (deadline
passed), `METHOD_VALIDATION_FAILED` (a missing or invalid parameter, `field` names it, the value is never echoed).

## 4. Registration

```csharp
// Program.cs of the service that owns the model
builder.Services.AddDistributionEventHub(c =>
{
    c.UniqueServiceAddress = "accounts-services";
    c.AddCkMethodHandlerDiscovery();            // answers dispatcher discovery with an immediate announcement
});
builder.Services.AddCkMethodHandlers("accounts-services",
    m => m.Handle<ChangePasswordHandler>(SampleAccountsCkIds.AccountChangePasswordMethodId));
```

At startup (order 15, before the bus) every registration is checked against the CK cache of the system tenant
(`Methods:ValidationTenantIds` to name other tenants): unknown method id, a parameter without a property on the parameters
record, a scalar type mismatch, result vs. no-result handler. A mismatch **stops the service** with a message that names the
method. The host then connects one shared command endpoint per method (competing consumers, faults suppressed), **waits until every
endpoint accepts messages** (`EndpointHandle.WaitUntilReadyAsync`, at most `Methods:EndpointReadyTimeout`, default 30 s; the
service fails to start instead of announcing a handler that cannot receive), and only then announces the handler
(`CkMethodHandlerAnnounced`). It repeats the announcement every 15 s (TTL 45 s) and withdraws on graceful shutdown before it
removes the queues.

Options (section `Methods`): `HeartbeatInterval`, `Ttl`, `ValidateRegistrations` (default `true`, switch off only in tests),
`ValidationTenantIds`, `EndpointReadyTimeout`.

## 5. Logging and sensitive parameters

The host logs method id, invocation id, tenant and correlation id, never `ParametersJson` or the token. Values of parameters
marked `sensitive: true` in the model, and the caller token, are masked (`***`) in an exception message a handler throws
before the host logs it. Do not log `context.Parameters` yourself; the generated records print `***` for sensitive members.

## 6. Unit-testing a handler

```csharp
var request = new CkMethodRequest("tenant") { InvocationId = Guid.NewGuid(), MethodId = id, CkTypeId = typeId,
    TargetRtId = "acc-1", DeadlineUtc = DateTime.UtcNow.AddSeconds(15) };
var context = new CkMethodContext<AccountChangePasswordParameters>(request,
    new AccountChangePasswordParameters { NewPassword = "short" });
var outcome = await new ChangePasswordHandler(store).HandleAsync(context, CancellationToken.None);
Assert.Equal("PASSWORD_TOO_SHORT", outcome.Failure!.Code);
```

## 7. For transport authors

`ICkMethodHandlerInvoker.InvokeAsync(CkMethodRequest)` is the transport-independent core: version check, deadline,
scope, parameter and result JSON (`CkMethodJson.Options`), error mapping, redaction. The bus consumer is a thin shell around it;
the in-process transport (section 8) calls the same core. It never throws for a failing request.

## 8. In-process handlers (methods the calling service owns)

A service that implements a method itself and also calls it (the asset-repo gateway for `Tenant.*`, `Archive.*`) registers the
**same handler class** in-process: no queue, no announcement, no bus hop, no event hub needed.

```csharp
builder.Services.AddCkInProcessMethodHandlers(
    m => m.Handle<ChangePasswordHandler>(SampleAccountsCkIds.AccountChangePasswordMethodId));

// at the call site (a GraphQL resolver, a controller, a bot job)
public sealed class Caller(ICkInProcessMethodTransport methods)
{
    public async Task<CkMethodResponse> ChangeAsync(CkMethodRequest request, CancellationToken ct) =>
        methods.CanHandle(request.MethodId)
            ? await methods.InvokeAsync(request, ct)
            : throw new InvalidOperationException("remote method: use the dispatcher");   // F3.3 decides
}
```

* **Same answer as on the bus.** The transport uses the same invoker core: contract version check, deadline,
  parameter/result JSON (`{"value": ...}`, `CkMethodJson.Options`), declared errors, `METHOD_HANDLER_FAILED` with a
  `correlationId`, redaction. `InProcessTransportTests` compares the serialized envelopes of both paths. Only
  `handlerInstanceId` differs (it names the answering host).
* **Deadline.** `DeadlineUtc` works as on the bus: the handler token fires there, and a handler that ignores it is not waited for;
  the caller gets `METHOD_TIMEOUT`. The caller's own token surfaces as `OperationCanceledException`.
* **Scope and transaction.** Each call gets a new DI scope of the root provider, not the scope of the caller's HTTP request,
  and runs outside the caller's database transaction (decision 2026-10-07). The handler does not see the request's scoped services.
* **Identity.** Put `Caller` and `CallerAccessToken` into the request exactly as for a remote call; the handler receives them
  unchanged and a handler must still re-validate the token. Neither value is ever logged.
* **One place per method.** A method registered in-process and as a remote handler stops the startup. Not announced, so a
  remote dispatcher never routes to it.

### Invocation pipeline: extension points for authorization, audit and idempotency

Every transport call runs through `ICkMethodInvocationPipeline`; lower `Order` is outer. Audit (100) wraps authorization (200), which wraps idempotency (300), which wraps the transport: audit records every call including refusals and replays, and a stored outcome is replayed only for an authorized caller.

 add steps with `AddCkMethodInvocationFilter<TFilter>()`.

```csharp
public sealed class MyAuditFilter : ICkMethodInvocationFilter
{
    public int Order => CkMethodInvocationFilterOrder.Audit;   // Audit 100 (outermost) < Authorization 200 < Idempotency 300 (innermost)

    public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        var response = await next(context.Request);            // or return a failed response without calling next
        // record context.Transport ("in-process" | "bus"), context.Request.InvocationId, response.Success / Error.Code
        return response;
    }
}
```

A filter refuses with a normal failed `CkMethodResponse` (for example `METHOD_FORBIDDEN`), may continue with a modified request
(`next(request with { IdempotencyKey = ... })`) and must not log `ParametersJson` or the token. An exception escaping a filter is
answered `METHOD_HANDLER_FAILED`. The dispatcher of F3.3 runs its bus send as the terminal delegate of the same pipeline
(`pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request, send, ct)`), so authorization and audit see local and remote calls alike.

## 8. Duplicate deliveries and failing sends

The bus consumer receives the transport delivery info (`CommandDeliveryInfo`). A **possible duplicate** (the broker redelivers
because the previous consumer lost its connection before the acknowledgement, or the bus retries) is **not executed**: the
previous delivery may have run the method. The caller gets `METHOD_OUTCOME_UNKNOWN` (see the wire contract). The host does not
know which methods are idempotent, so every method is treated as non-idempotent. The invocation id check stays as a second
line (a repeated id is answered `METHOD_OUTCOME_UNKNOWN` as well, also after a refused redelivery).

A sender must use the shared-endpoint send, `IDistributionEventHubService.GetCommandResponseAsync(queue, request,
new CommandSendOptions { Mandatory = true, Timeout = ..., TimeToLive = ... }, ct)`. With `Mandatory`, a request to a queue
without any consumer fails at once with `CommandNotRoutableException` (typically within milliseconds) instead of ending in
`METHOD_TIMEOUT`; the dispatcher maps it to `METHOD_HANDLER_NOT_AVAILABLE`. The plain `GetCommandResponseAsync` overload does
not reach the shared endpoint. Handler endpoints suppress faults: an exception escaping the consumer is logged without the
message, is not retried and leaves no `_error` / `_skipped` copy of the request (it holds passwords and tokens).

## 9. Calling a method: the dispatcher (F3.3, AB#6905)

`ICkMethodDispatcher` is the one entry point for a gateway (GraphQL gateway, pipeline node, REST facade). The caller never
chooses a transport; the dispatcher does, and the answer is always the same `CkMethodResponse` envelope.

```csharp
// Program.cs of the calling service
services.AddDistributionEventHub(c =>
{
    c.UniqueServiceAddress = "asset-repo";
    c.AddCkMethodHandlerRegistryFeed();            // announce / withdraw broadcasts fill the dispatcher's registry
});
services.AddCkMethodDispatcher();                   // dispatcher + registry + the shared invocation pipeline
services.AddCkMethodInvocationFilter<MyAuthorizationFilter>();   // filters: see section 8, run for both transports

// at the call site
CkMethodResponse response = await dispatcher.InvokeAsync(request, cancellationToken);
```

**What `InvokeAsync` does, in this order**

1. **Resolve the declaring element.** A method is routed by the element that *declares* it. Methods cannot be overridden (CK
   message 100), so there is exactly one. When `request.CkTypeId` is a derived type, or a type that implements the interface that
   carries the method, the dispatcher looks the method up in the CK cache of the tenant (`CkTypeGraph.AllMethods`: the engine
   records the base type for an inherited method and the highest implementing type for an interface method) and rewrites
   `request.MethodId` to the qualified id of the declaring element, for example `Fleet/Car.Start-1` becomes
   `Fleet/Vehicle.Start-1`. This happens before the pipeline, so filters, the queue name and the handler registration all see the
   same id. A type or method the cache does not know (or a service without CK cache) is routed as given.
2. **Prefer in-process.** A method registered with `AddCkInProcessMethodHandlers` in the same service runs through
   `ICkInProcessMethodTransport` (pipeline transport name `in-process`); the bus and the registry are not touched.
3. **Otherwise the bus.** `ICkMethodInvocationPipeline.ExecuteAsync("bus", request, send, ct)` where `send` is the dispatcher's
   only own code: one mandatory, non-persistent `GetCommandResponseAsync` to the shared queue `CkMethodQueueNames.For(methodId)`.
   Handlers consume that queue as competing consumers, so replicas of one service share the traffic.

The dispatcher adds **no filter and no pipeline of its own**: audit, authorization and idempotency are `ICkMethodInvocationFilter`s
and run once per call for both transports.

**Outcomes (the caller sees an envelope, never an exception)**

| Situation | Envelope |
|---|---|
| Handler answered | the handler's `CkMethodResponse` unchanged (success, declared error, `METHOD_HANDLER_FAILED`, `METHOD_TIMEOUT`, `METHOD_OUTCOME_UNKNOWN`, ...) |
| Nobody consumes the queue (never created, or the consumers are gone) | `METHOD_HANDLER_NOT_AVAILABLE`, parameters `methodId` and `reason` = `no-live-handler`, within milliseconds, no stack trace |
| The registry knows live handlers, none serves the tenant | `METHOD_HANDLER_NOT_AVAILABLE`, `reason` = `tenant-not-served`, nothing sent |
| The request carries a newer contract version than every live handler announced | `METHOD_CONTRACT_VERSION_UNSUPPORTED`, nothing sent (wire contract rule 4) |
| No answer before `DeadlineUtc` + `ResponseGrace` (2 s), or the deadline has passed already | `METHOD_TIMEOUT` (outcome unknown), nothing is re-sent |
| Bus fault, bus stopping, filter or resolver exception | `METHOD_HANDLER_FAILED` with `correlationId` (details only in the log) |
| Caller cancels its token | `OperationCanceledException` (the only exception) |

**Timeouts.** The deadline is `request.DeadlineUtc`, the handler's cancellation token fires at it and the handler answers
`METHOD_TIMEOUT` itself. The dispatcher waits `ResponseGrace` longer (`Methods:Dispatcher:ResponseGrace`, default 2 s) before it
answers `METHOD_TIMEOUT` itself; message TTL and send timeout are the same value. `Methods:Dispatcher:MaxTimeout` (default 10
minutes, the CK compiler allows 300 s) clamps an absurd deadline first, and the handler sees the clamped deadline. `DateTime.MaxValue`
is therefore served, not thrown on. Reading the timeout of a method from the CK definition and the further timeout rules are AB#6906.

**The registry is advisory.** `ICkMethodHandlerRegistry` (fed by `CkMethodHandlerAnnounced` / `Withdrawn`, expiry = receipt time + the
announced TTL, 45 s by default; a dispatcher asks for a fresh announcement at start) provides the tenant and contract version
checks and diagnostics (`GetLiveHandlers`, `GetLastSeen`, `Snapshot`; the persisted snapshot and the Studio view are F3.2). It never
vetoes a send: an empty registry (dispatcher just started, heartbeat lost) must not refuse a call the broker can still deliver, so the
mandatory flag decides. A withdrawal beats any announcement written before it; a broker "not routable" expires only entries last seen
before the send started.

**A handler stopping mid-flight.** A graceful stop withdraws first and removes the endpoints; new calls fail at once with
`METHOD_HANDLER_NOT_AVAILABLE`. A call that is running on that instance gets its cancellation token cancelled and ends as
`METHOD_TIMEOUT` (outcome unknown) or, when the handler finished first, with its result. It is never lost silently and never run twice.

**Tests.** `tests/Methods.Tests/Dispatching` (dispatcher with a faked hub, registry races, declaring-element resolution, registration)
and `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.cs` (private RabbitMQ: the same cases over the bus and in-process,
no consumer, consumer gone, mid-flight stop, timeout, derived type, replicas).
