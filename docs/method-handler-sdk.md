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
method. The host then connects one shared queue per method, announces the handler (`CkMethodHandlerAnnounced`), repeats the
announcement every 15 s (TTL 45 s) and withdraws on graceful shutdown before it removes the queues.

Options (section `Methods`): `HeartbeatInterval`, `Ttl`, `ValidateRegistrations` (default `true`, switch off only in tests),
`ValidationTenantIds`.

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
an in-process transport calls the same interface. It never throws for a failing request.

## Known limitations

The event hub on `main` exposes no endpoint readiness, no broker redelivery flag and no fault suppression yet, so the host
announces right after connecting the queues and detects duplicates by invocation id only. See `CLAUDE.md`.
