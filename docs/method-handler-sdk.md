---
description: Method handler guide entry, with a working sample: model, typed handler, errors, registration, unit tests; lists all parts.
background: true
---

# Implementing a method handler (bus transport)

Package `Meshmakers.Octo.Services.Methods`. Wire format: `method-wire-contract.md`.

A CK method is declared in a construction kit model. The service that owns the model implements it with a **typed
handler**. You write the handler class and one line of registration; queues, JSON, announcements and heartbeats belong to
the host.

## Parts of this guide

This file covers the model, the handler, errors, registration and unit tests. The other parts:

* [method-handler-sdk-caller.md](method-handler-sdk-caller.md): who the caller is (token validation in a remote handler), the
  declared authorization at the gateway and at the host, logging and sensitive parameters and results.
* [method-handler-sdk-in-process.md](method-handler-sdk-in-process.md): in-process handlers of the calling service and the
  invocation pipeline (filters, facts).
* [method-handler-sdk-idempotency.md](method-handler-sdk-idempotency.md): idempotency store contract, parameter fingerprint
  and the idempotency filter.
* [method-handler-sdk-async.md](method-handler-sdk-async.md): asynchronous handlers, the accepted outcome, the durable operation
  record, the store contract, options and the metric.
* [method-handler-sdk-delivery.md](method-handler-sdk-delivery.md): duplicate deliveries and failing sends, the dispatcher
  (routing, outcomes, timeouts, registry) and replicas.

## 1. What the model generates

For a method `ChangePassword-1` of type `Account` the CK source generator emits (see the CK language 2 guide, "Generated
Code"): `AccountChangePasswordParameters` (required parameters are `required` properties), `AccountChangePasswordResult { Value }`
when the method has a result, and the method id constant in the model's `*CkIds` class, for example
`Sample.Accounts/Account.ChangePassword-1`. Use the constant; never type the id by hand.

**Record parameters bind as the generated `Rt{Record}Record`.** A parameter of value type `Record` is the model's
runtime record, an attribute bag. `CkMethodJson.Options` binds it with its CK attributes only: the bookkeeping members
`attributes` and `ckRecordId` are neither read nor written, and an array attribute (`IAttributeValueList<T>`, an interface
System.Text.Json cannot create on its own) is read as a list into the record. So a record that holds a record array, a string
array or an int array works as a handler parameter type; write no plain look-alike classes. A missing mandatory array reads as
an empty list (the generated getter), a missing optional one as `null`, an explicit `[]` as an empty list. A record result is
written the same way (`{"path":...,"indexed":...}`).

**The result on the wire is the bare `Value`.** The host serializes only `Value` of the result record with
`CkMethodJson.Options`: `new AccountChangePasswordResult { Value = 12 }` becomes `resultJson` `12`, a String `"abc"`, a record
`{"name":...}`; never `{"value": 12}`. A null `Value` is a success without `resultJson`. The registration refuses a result type
without a public `Value` property, and the startup validation refuses a `Value` whose scalar type does not fit the declared result
(`the result is String in the model but property X.Value is Int32`). Members you add to the partial result record are not sent.

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
* `context` carries the validated caller `CallerPrincipal` ([caller identity](method-handler-sdk-caller.md#who-is-the-caller-token-validation-in-a-remote-handler)), the informational `Caller`, `TenantId`, `CkTypeId`,
  `TargetRtId` (null for static methods), `CorrelationId`, `IdempotencyKey`, `DeadlineUtc`/`RemainingTime` and `CallerAccessToken`. It has **no database session and
  no transaction**: a method runs outside the request transaction of the caller, open your own unit of work.
* **Authorization**: the CK model declares who may call. When a handler decides anything by who the caller is, it uses
  `context.CallerPrincipal` and nothing else: the host validated the token behind it ([caller identity](method-handler-sdk-caller.md#who-is-the-caller-token-validation-in-a-remote-handler)). Never trust `Caller` (it is
  not authenticated on the bus) and do not parse `CallerAccessToken` yourself.
* **Cancellation and deadline**: `cancellationToken` is cancelled at `DeadlineUtc`. Pass it to every awaited call. A handler that
  ignores it and finishes late still answers late; the caller has then already received `METHOD_TIMEOUT`.

## 3. Errors

The wire carries a stable **code and parameters, never message text**; clients render their own text.

| You do | The caller gets |
|---|---|
| `return CkMethodOutcome<T>.Fail("MY_CODE", ("name", "value"))` or `throw new CkMethodException("MY_CODE", ...)` | `error.code = MY_CODE`, `error.parameters` |
| `ValidationFailed(field, reason)`, `TargetNotFound()`, `Forbidden(reason)` | `METHOD_VALIDATION_FAILED` / `METHOD_TARGET_NOT_FOUND` / `METHOD_FORBIDDEN` with `methodId` |
| a write the database did not acknowledge in time: a MongoDB write-concern error (e.g. the 30 s `wtimeout` of `w:majority`, code 64) or an `UnknownTransactionCommitResult` commit error, also wrapped by the repository (rule R3) | `METHOD_OUTCOME_UNKNOWN` with `correlationId`: the write may still be on the primary, so the caller must check the state; the exception is logged with that id |
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
(`Methods:ValidationTenantIds` to name other tenants; ids are matched case-insensitively against the CK cache, so the default
`OctoSystem` works as is): unknown method id, a parameter without a property on the parameters
record, a scalar type mismatch, result vs. no-result handler. A mismatch **stops the service** with a message that names the
method.

**The CK cache is loaded for the check.** A CK model import at startup (this replica's own, or the reload after another
replica imported first) unloads the tenant's CK cache, and the check runs right after it. The check therefore first loads the CK cache of
every validation tenant that is not loaded (through the tenant context of the service), or waits for it when a load fails because another
replica's import is still running. It retries until `Methods:CacheLoadTimeout` (default 2 min, environment variable
`OCTO_Methods__CacheLoadTimeout`, must be positive). When the cache is unloaded again while the registrations are checked, it loads
it again and repeats, within the same deadline. The check never treats an unloaded cache as "no such method" any more:

| Situation | Result |
|-----------|--------|
| Cache not loaded, loads within the timeout | the check runs, the service starts |
| Method really not in the loaded model (or the model is not installed) | start fails with the existing "... has no such method" error |
| Cache does not load within the timeout | start fails with "The CK cache of tenant '&lt;tenant&gt;' did not load ..." (`CkMethodDefinitionCacheNotLoadedException`), naming the tenant, the last load error and the `Methods:CacheLoadTimeout` setting |

So several replicas of a service with CK methods start on a fresh or freshly upgraded tenant without a service-specific reload or
retry. Tests: `tests/Methods.Tests/Handlers/CkMethodValidationCacheLoadTests.cs` (on-demand load, wait without a loader, unknown
method, timeout, unload between load and check, four replicas on one fresh tenant).

The host then connects one shared command endpoint per method (competing consumers, faults suppressed), **waits until every
endpoint accepts messages** (`EndpointHandle.WaitUntilReadyAsync`, at most `Methods:EndpointReadyTimeout`, default 30 s; the
service fails to start instead of announcing a handler that cannot receive), and only then announces the handler
(`CkMethodHandlerAnnounced`). It repeats the announcement every 15 s (TTL 45 s) and withdraws on graceful shutdown before it
removes the queues.

Options (section `Methods`): `HeartbeatInterval`, `Ttl`, `ValidateRegistrations` (default `true`, switch off only in tests),
`ValidationTenantIds`, `CacheLoadTimeout` (default 2 min), `EndpointReadyTimeout`.

**Heartbeat and TTL (final values).** The defaults are final: **`Methods:HeartbeatInterval` = 15 s, `Methods:Ttl` = 45 s**
(environment variables `OCTO_Methods__HeartbeatInterval` and `OCTO_Methods__Ttl`, format `hh:mm:ss`). Every announcement carries its own
interval and TTL, and a dispatcher expires an entry at *receipt time plus the announced TTL*, so hosts may run with different values.

* **Validated at startup** (`ValidateOnStart`): `HeartbeatInterval` >= 1 s, `Ttl` >= 2 x `HeartbeatInterval` and `Ttl` <= 24 h (the clamp
  of the registry). A host that violates a rule **does not start**; the message names the key and the rule, for example
  `Methods:Ttl is 00:00:20 but must be at least 2 x Methods:HeartbeatInterval (00:00:30)`. Before, an interval of 0 crashed the heartbeat
  loop and a TTL below two intervals let a live handler expire between two heartbeats.
* **Why 15 s / 45 s.** 45 s tolerates two lost heartbeats; shorter values only add bus traffic. The registry is the gate of
  the bus send ([the dispatcher](method-handler-sdk-delivery.md#calling-a-method-the-dispatcher)): a method without a live entry is refused before the send, a gracefully stopped handler withdraws at once, and a
  handler that crashed stays live for at most its TTL, during which the broker's mandatory flag still detects it (one returned send, then
  the entry is expired and the dispatcher asks every handler to announce itself again). A dispatcher's registry warm-up
  (`Methods:Dispatcher:RegistryWarmUp`, 20 s) must stay longer than the heartbeat interval of the hosts. After the last handler of a
  method went away, calls wait `Methods:Dispatcher:HandlerHandOver` (3 s) for a successor before they are refused.

## 5. Unit-testing a handler

```csharp
var request = new CkMethodRequest("tenant") { InvocationId = Guid.NewGuid(), MethodId = id, CkTypeId = typeId,
    TargetRtId = "acc-1", DeadlineUtc = DateTime.UtcNow.AddSeconds(15) };
var context = new CkMethodContext<AccountChangePasswordParameters>(request,
    new AccountChangePasswordParameters { NewPassword = "short" });
var outcome = await new ChangePasswordHandler(store).HandleAsync(context, CancellationToken.None);
Assert.Equal("PASSWORD_TOO_SHORT", outcome.Failure!.Code);
```

## 6. For transport authors

`ICkMethodHandlerInvoker.InvokeAsync(CkMethodRequest)` is the transport-independent core: version check, deadline,
scope, parameter and result JSON (`CkMethodJson.Options`), error mapping, redaction. The bus consumer is a thin shell around it;
the in-process transport ([in-process handlers](method-handler-sdk-in-process.md)) calls the same core. It never throws for a failing request.
