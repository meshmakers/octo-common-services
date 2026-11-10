---
description: Method handler guide: in-process handlers of the calling service, the invocation pipeline with its filter extension points and REST facades.
background: true
---

# Method handler: in-process handlers, the invocation pipeline and REST facades

Part of the method handler guide; start at [method-handler-sdk.md](method-handler-sdk.md), which lists all parts.

## In-process handlers (methods the calling service owns)

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
  parameter/result JSON (`CkMethodJson.Options`; the result is the bare `Value`, see [What the model generates](method-handler-sdk.md#1-what-the-model-generates)), declared errors, `METHOD_HANDLER_FAILED` with a
  `correlationId`, redaction. `InProcessTransportTests` compares the serialized envelopes of both paths. Only
  `handlerInstanceId` differs (it names the answering host).
* **Deadline.** `DeadlineUtc` works as on the bus: the handler token fires there, and a handler that ignores it is not waited for;
  the caller gets `METHOD_TIMEOUT`. The caller's own token surfaces as `OperationCanceledException`.
* **Scope and transaction.** Each call gets a new DI scope of the root provider, not the scope of the caller's HTTP request,
  and runs outside the caller's database transaction (decision 2026-10-07). The handler does not see the request's scoped services (see the next two points for the ambient context).
* **Detached from the caller's context (OD-8).** The handler runs in an execution flow that does **not** inherit the caller's
  `ExecutionContext`: an `AsyncLocal` the caller set (the asset-repo request session, a principal scope, a logging scope) is empty
  in the handler, also after its first `await`, and nothing the handler sets leaks back. A fresh DI scope alone would not do this,
  because ambient values travel with the async flow, not with the scope. Carried across explicitly: the trace (the handler span
  `CK method handle` of the `ActivitySource` `Meshmakers.Octo.Methods` is a child of the caller's activity; subscribe to the source to
  export it, the trace continues without a listener), `CultureInfo.CurrentCulture` / `CurrentUICulture`, the cancellation token with
  the deadline, and the logging scope that identifies the call (`MethodId`, `InvocationId`, `CorrelationId`, `TenantId`). A library
  that reads another `AsyncLocal` inside a handler sees the default; pass what a handler needs in the request (`Caller`, parameters).
* **Tripwire.** A host registers `services.AddCkInProcessAmbientStateProbe<MyProbe>()` for each kind of request-bound ambient state it
  owns; `MyProbe : ICkInProcessAmbientStateProbe` answers `IsRequestStateVisible(IServiceProvider handlerScope)` with yes/no (the
  asset-repo probe asks `IOctoSessionAccessor.HasSession`). It is asked in the handler's flow and new scope, before the handler is
  created. `true` (or an exception) fails the call with `METHOD_HANDLER_FAILED` (`methodId`, `correlationId`), the handler does not run,
  and the log names the probe type, never data. With the detach in place the answer must always be `false`; a `true` is a regression.
  A probe is a singleton, thread-safe, cheap, and never logs or returns what it looked at.
* **Identity.** Put `Caller` and `CallerAccessToken` into the request exactly as for a remote call; the handler receives them
  unchanged and a handler must still re-validate the token. Neither value is ever logged.
* **One place per method.** A method registered in-process and as a remote handler stops the startup. Not announced, so a
  remote dispatcher never routes to it.

## Invocation pipeline: extension points for authorization, audit and idempotency

Every transport call runs through `ICkMethodInvocationPipeline`; lower `Order` is outer. Audit (100) wraps authorization (200), which wraps idempotency (300), which wraps the transport: audit records every call including refusals and replays, and a stored outcome is replayed only for an authorized caller.
Add steps with `AddCkMethodInvocationFilter<TFilter>()`. The filters of this library are described in
[Authorization](method-handler-sdk-caller.md#authorization-the-declared-authorization-is-enforced-for-every-call-f34) (band 200)
and [Idempotency filter at band 300](method-handler-sdk-idempotency.md#idempotency-filter-at-band-300); audit is in `method-audit.md`.

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

`context.Request` is the redacted view (sensitive parameter values `"***"`, token `null`): a filter cannot leak what it cannot see, so an audit
filter may serialise it. A filter that passes it (or a copy) to `next` continues with the raw values for the transport; a filter that
edits `ParametersJson` gets its edit used, **merged per member**: a sensitive member that is still `"***"` in the filter's result is
restored from the raw request, so editing, normalizing or enriching a non-sensitive member never sends the placeholder to the handler. A
sensitive member the filter gives another value is an explicit replacement and is used as is; a removed member stays removed. A `"***"` for a
sensitive member that the request does not carry cannot be restored: the call is refused with `METHOD_VALIDATION_FAILED` (`field` = the
member) and the handler is not called. (A filter cannot tell the placeholder from a real value `"***"` of a sensitive parameter; the raw
value wins.) It follows that a filter cannot fingerprint the real parameter values.

The response mirrors this: the filters get the response of the transport with `ResultJson` replaced by `"***"` when the CK result is
`sensitive` (or the method is unknown), and the real response goes back to the caller as long as the filters pass the view through. A
filter that returns a different response (for example a failed one) keeps its change; a filter that answers without calling `next` is returned
as is.

**Sharing facts between filters.** `context.Facts` (`CkMethodInvocationFacts`) is one object per invocation, created by the pipeline:
every filter of that call sees the same instance (also after `context with { Request = ... }`), two calls never share one. An inner filter tells
the outer ones what it did, and the outer one reads it **after `next()` returned**: the idempotency filter (band 300) sets `Facts.ReplayOf` (the
first invocation id of the key) when it answers from its store, and the audit filter (band 100) reads it to write "replay of". Only the idempotency
filter sets `ReplayOf`. `Facts.ParametersFingerprint` (the keyed fingerprint of the raw parameters, an opaque `CkMethodParametersFingerprint`) is
set by the pipeline before the first filter runs and is read-only for filters. The facts are not a member of `CkMethodRequest` or
`CkMethodResponse`, are never serialized and never logged: the `ToString()` of the facts, of the fingerprint and of the context prints no
fingerprint value, and a filter must not log or audit it either.

A filter refuses with a normal failed `CkMethodResponse` (for example `METHOD_FORBIDDEN`), may continue with a modified request
(`next(request with { IdempotencyKey = ... })`) and must not log `ParametersJson` or the token. An exception escaping a filter is
answered `METHOD_HANDLER_FAILED` (`METHOD_OUTCOME_UNKNOWN` when it is an unacknowledged write, see [Errors](method-handler-sdk.md#3-errors); the audit event
records the same code). The dispatcher of F3.3 runs its bus send as the terminal delegate of the same pipeline
(`pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request, send, ct)`), so authorization and audit see local and remote calls alike.

## REST facades: a service calls a method it hosts itself

A service that hosts a method handler can serve the same operation through its own REST endpoint (a *facade*), for example the
identity service's user endpoints over the `User.*` methods. The facade calls the method through `ICkMethodDispatcher` like any other
caller, and the handler runs **in this process** through the same invocation core as a call from another service: declared
authorization (`ICkMethodAuthorizationEvaluator`), parameter validation, redaction of `sensitive` parameters, audit and `ck.method.*`
metrics (transport `in-process`). No bus hop, so no parameter (a password) reaches the broker.

**Two registrations, one rule.**

| Registration | Announced, bus queue | Another process calls it | This process calls it |
|---|---|---|---|
| `m.Handle<THandler>(methodId)` in `AddCkMethodHandlers` | yes | over the bus | in-process (no send) |
| `m.HandleLocalOnly<THandler>(methodId)` in `AddCkMethodHandlers` | **no** | `METHOD_HANDLER_NOT_AVAILABLE` (no queue) | in-process |
| `m.Handle<THandler>(methodId)` in `AddCkInProcessMethodHandlers` | no | `METHOD_HANDLER_NOT_AVAILABLE` | in-process |

`HandleLocalOnly` is for a method whose bus exposure is switched off but whose facade must keep working, e.g. the identity password
methods before the broker is protected (B0): `EnablePasswordMethods ? m.Handle<H>(id) : m.HandleLocalOnly<H>(id)`. A method is
registered either for the bus or local only: both for the same id stop the start (`CkInProcessMethodValidationService`, order 15).
A bus-registered method called by its own host answers with the **announced** handler instance id, so the envelope is byte-identical to
the one a remote caller gets from that host (tests compare success, declared error, `METHOD_FORBIDDEN` and `METHOD_HANDLER_FAILED`).

**The caller.** The local path validates no token: the principal is the one the facade establishes with `CkMethodPrincipalScope`
(from the authenticated HTTP user). Without a scope there is no principal and the declared authorization refuses the call
(`METHOD_FORBIDDEN`); nothing falls back to a system identity. The principal needs the scope `octo_api` like every method caller.

**The result.** `CkMethodFacadeResults.ToActionResult(response, options)` (`Meshmakers.Octo.Services.Methods.Facades`) maps the
envelope to an MVC `IActionResult`:

| Code | Default status |
|---|---|
| success with result / without result | `200` with the result JSON / `204` |
| accepted (asynchronous call, `Operation` set) | `202` with the operation handle |
| `METHOD_VALIDATION_FAILED` | 400 |
| `METHOD_FORBIDDEN` | 403 |
| `METHOD_TARGET_NOT_FOUND` | 404 |
| `METHOD_REQUEST_IN_PROGRESS` | 409 |
| `METHOD_PARAMETERS_TOO_LARGE` | 413 |
| `METHOD_HANDLER_FAILED`, `METHOD_OUTCOME_UNKNOWN`, an unknown `METHOD_*` code | 500 |
| `METHOD_HANDLER_NOT_AVAILABLE`, `METHOD_CONTRACT_VERSION_UNSUPPORTED`, `METHOD_NOT_SUPPORTED_BY_HANDLER_VERSION` | 503 |
| `METHOD_TIMEOUT` | 504 |
| a code the method declares | 400 |

The default error body is problem details (`application/problem+json`) with the code as `title` and the extensions `code`,
`parameters` and `details`. Error parameters never carry parameter values (wire contract), so the body is safe. An endpoint keeps
its existing status codes and bodies with `CkMethodFacadeOptions`: `MapError(code, status)`, `MapError(code, error => result)` (also
for platform codes) and `MapSuccess(response => result)`. Build the options once per endpoint.

```csharp
// Program.cs of the identity service
services.AddCkMethodHandlers("IdentityService", m =>
{
    m.Handle<UserSetRolesHandler>(SystemIdentityCkIds.UserSetRolesMethodId);
    if (options.EnablePasswordMethods) m.Handle<UserResetPasswordHandler>(SystemIdentityCkIds.UserResetPasswordMethodId);
    else m.HandleLocalOnly<UserResetPasswordHandler>(SystemIdentityCkIds.UserResetPasswordMethodId);
});
services.AddCkMethodDispatcher();   // the facade's entry point; local methods never touch the bus

// UsersController
private static readonly CkMethodFacadeOptions ResetPasswordFacade = new CkMethodFacadeOptions()
    .MapError("PASSWORD_POLICY_VIOLATED", error => new BadRequestObjectResult(error.Details))
    .MapSuccess(_ => new NoContentResult());

[HttpPost("{id}/reset-password")]
public async Task<IActionResult> ResetPassword(string tenantId, string id, ResetPasswordDto dto)
{
    var request = new CkMethodRequest(tenantId)
    {
        InvocationId = Guid.NewGuid(),
        MethodId = SystemIdentityCkIds.UserResetPasswordMethodId,
        CkTypeId = "System.Identity/User",
        TargetRtId = id,
        ParametersJson = JsonSerializer.Serialize(new UserResetPasswordParameters { NewPassword = dto.Password }, CkMethodJson.Options),
        DeadlineUtc = timeouts.GetDeadlineUtc(tenantId, "System.Identity/User", SystemIdentityCkIds.UserResetPasswordMethodId)
    };
    using (CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(User)))
    {
        var response = await dispatcher.InvokeAsync(request, HttpContext.RequestAborted);
        return response.ToActionResult(ResetPasswordFacade);
    }
}
```

`AddCkMethodHandlers` registers the local transport by itself; `AddCkMethodDispatcher` (with `AddCkMethodHandlerRegistryFeed`) gives
the facade its `ICkMethodDispatcher`. A method the service does not host still goes over the bus; check
`ICkInProcessMethodTransport.CanHandle(methodId)` at start if a facade must never leave the process.

**Tests.** `tests/Methods.Tests/InProcess/SelfHostedMethodCallTests.cs` (no send to the broker, transport in metric and audit,
byte-identical envelopes, local only not announced, one-place rule, missing principal refused, redaction canary over logs, spans,
filters and audit), `tests/Methods.Tests/Facades/CkMethodFacadeResultsTests.cs` (every code, overrides) and
`tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.SelfHosted.cs` (real broker: byte-identical to a remote call of the same
host; local only = no queue).
