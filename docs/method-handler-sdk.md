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
* `context` carries the validated caller `CallerPrincipal` (section 4a), the informational `Caller`, `TenantId`, `CkTypeId`,
  `TargetRtId` (null for static methods), `CorrelationId`, `IdempotencyKey`, `DeadlineUtc`/`RemainingTime` and `CallerAccessToken`. It has **no database session and
  no transaction**: a method runs outside the request transaction of the caller, open your own unit of work.
* **Authorization**: the CK model declares who may call. When a handler decides anything by who the caller is, it uses
  `context.CallerPrincipal` and nothing else: the host validated the token behind it (section 4a). Never trust `Caller` (it is
  not authenticated on the bus) and do not parse `CallerAccessToken` yourself.
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

**Heartbeat and TTL (final values, AB#7838).** The defaults are final: **`Methods:HeartbeatInterval` = 15 s, `Methods:Ttl` = 45 s**
(environment variables `OCTO_Methods__HeartbeatInterval` and `OCTO_Methods__Ttl`, format `hh:mm:ss`). Every announcement carries its own
interval and TTL, and a dispatcher expires an entry at *receipt time plus the announced TTL*, so hosts may run with different values.

* **Validated at startup** (`ValidateOnStart`): `HeartbeatInterval` >= 1 s, `Ttl` >= 2 x `HeartbeatInterval` and `Ttl` <= 24 h (the clamp
  of the registry). A host that violates a rule **does not start**; the message names the key and the rule, for example
  `Methods:Ttl is 00:00:20 but must be at least 2 x Methods:HeartbeatInterval (00:00:30)`. Before, an interval of 0 crashed the heartbeat
  loop and a TTL below two intervals let a live handler expire between two heartbeats.
* **Why 15 s / 45 s.** The registry is advisory: whether a call fails fast (`METHOD_HANDLER_NOT_AVAILABLE`, under a second) is decided by the
  broker's mandatory send, not by the TTL. The TTL only drives diagnostics and the persisted snapshot. 45 s tolerates two lost heartbeats;
  shorter values only add bus traffic.

## 4a. Who is the caller? Token validation in a remote handler (AB#6913)

The method bus is internal but **not authenticated per message**: anything that can publish to the method queue can write any
`Caller` into a request. So the host re-validates the access token that the caller forwarded (`CallerAccessToken`) before the handler
runs, with the same rules as the HTTP API of your service. Switch it on once per service:

```csharp
builder.Services.AddCkMethodHandlers("accounts-services", m => m.Handle<ChangePasswordHandler>(SampleAccountsCkIds.AccountChangePasswordMethodId));
builder.Services.AddCkMethodCallerTokenValidation(o => o.Authority = builder.Configuration["Identity:Authority"]);   // the authority of your HTTP API
```

With it, a call without a valid token is answered `METHOD_FORBIDDEN` **before the parameters are even read**, and the handler is not
called. A valid token reaches the handler as `context.CallerPrincipal` (`Subject`, `SubjectId`, `ClientId`, `Roles`, `Scopes`,
`TenantId`, `IsServiceToken`):

```csharp
var caller = context.CallerPrincipal!;          // never null on a host that enabled the validation
if (!caller.Roles.Contains("AccountManagement", StringComparer.OrdinalIgnoreCase) && caller.Subject != context.TargetRtId)
{
    return CkMethodOutcome<AccountChangePasswordResult>.Forbidden("not-owner");
}
```

What is checked (defaults mirror the HTTP API; section `Methods:Token`):

| Check | Default | Setting |
|---|---|---|
| issuer and signature | issuer = `Authority` with trailing slash; keys from the identity JWKS (`.well-known/openid-configuration`), cached, refreshed once per 5 min when a token carries an unknown key id | `Authority`, `ValidIssuer`, `MetadataAddress`, `KeyAutomaticRefreshInterval`, `KeyRefreshInterval` |
| algorithm | asymmetric only (RS/PS/ES); `none` and `HS*` are never accepted | `ValidAlgorithms` |
| expiry | required, 30 s clock skew | `ClockSkew` |
| scope | `octo_api` | `RequiredScope` |
| tenant | the `tenant_id` claim equals the tenant of the request (no parent-tenant rule; `allowed_tenants` is a selection hint and is not consulted) | - |
| audience | **not validated** (like the HTTP API, where `ValidateAudience` is off). Set `ValidAudiences` to require one of them | `ValidAudiences` |

**With `ValidAudiences` empty, every `octo_api` token of the tenant is accepted**, whichever API it was issued for. Set the audience as
soon as the identity service issues audience-scoped tokens.

**Service tokens.** A client-credentials token has no `sub`. It is accepted when `client_id` is present and `tenant_id` equals the tenant
of the request, or when the client is listed in `TenantAuthorization:CrossTenantServiceClientIds` (the same list the HTTP API uses). The
validation always enforces; the `LogOnly` and `Disabled` modes of the HTTP side are not applied. For such a caller
`CallerPrincipal.Subject` is the client id, `SubjectId` is `null` and `IsServiceToken` is `true`.

**Refusals.** `METHOD_FORBIDDEN` with the additive `reason` parameter:

| `reason` | Meaning | The caller should |
|---|---|---|
| `token-missing` | the request carries no token | send the access token |
| `token-expired` | expired beyond the skew, but otherwise a valid token for this tenant | retry once with a fresh token |
| `token-invalid` | malformed, unsigned, foreign issuer or key, missing scope, or no `sub`/`client_id` | not retry with this token |
| `tenant-not-allowed` | issued for another tenant | not retry; sign in to the tenant |
| `audience-mismatch` | an audience is configured and the token has none of them | not retry |

An expired token is only reported as `token-expired` if it is genuine otherwise; an expired forgery is `token-invalid`. If the signing
keys cannot be loaded at all (identity service down and nothing cached) the call is answered `METHOD_HANDLER_FAILED`, never accepted.

**Not covered.** The in-process transport does not validate a token: it runs in the process that already authenticated the caller and
hands the principal of that process (`CkMethodPrincipalScope`) to the handler as `CallerPrincipal`. A remote service that does not call
`AddCkMethodCallerTokenValidation` has no validated caller, so the host refuses every call (`METHOD_FORBIDDEN`, see "The host enforces the declared authorization"). A handler that receives the raw token can replay it
against the APIs until it expires (about an hour by default); an unauthenticated handler announcement on the bus is a separate
decision (AB#7039). Switching the validation off (`Methods:Token:DisableValidation`) is a test configuration; the service refuses to
start outside the `Development` environment. Nothing from the token (value, claims, key id) is ever logged or put into an error.

Unit-test a handler by giving the context a principal: `new CkMethodContext<TParameters>(request, parameters) { CallerPrincipal = new CkMethodPrincipal("user-1", ["AccountManagement"], ["octo_api"]) }`.

## 5. Logging and sensitive parameters

The host logs method id, invocation id, tenant and correlation id, never `ParametersJson` or the token. Values of parameters
marked `sensitive: true` in the model, and the caller token, are masked (`***`) in an exception message a handler throws
before the host logs it. Do not log `context.Parameters` yourself; the generated records print `***` for sensitive members.

Filters and the audit trail never see those values either (AB#6914): the invocation context hands every filter a **redacted view** of the
request. Sensitive parameter values are `"***"`, the caller token is `null`, a payload that cannot be parsed is `{}`, and a method that is
not known is redacted completely. Only the transport (and so the handler) gets the raw values. A validation error names the parameter
(`field`) and never echoes its value; exceptions logged by the pipeline and the dispatcher are masked the same way.

The same holds for the **result** of a method whose CK result is marked `sensitive` (AB#7007): every filter sees
`ResultJson == "\"***\""` (a JSON string, so it stays valid JSON; a method that is not known is redacted completely), the caller gets the real
result, and the host never writes it into a log line, an exception message, an activity tag or a metric label. `CkMethodResponse.ToString()`
never prints the result. Error responses are not touched. Limits: a record result is masked as a whole, and a method that is implemented as a pipeline
refuses a sensitive result (the pipeline execution stores its output).

The masking of exception text covers every form in which a serializer or a URL can write a value (AB#7015), not only the decoded text:
the plain value, the JSON-escaped forms (System.Text.Json default and relaxed encoder, Newtonsoft.Json: `\"`, `\\`, `\uXXXX` for quote,
plus, ampersand, angle brackets, apostrophe, control characters and non-ASCII characters in upper or lower hex case, escaped slash), the
same once more for JSON embedded in a JSON string, and the percent-encoded form. So a password such as `pa"ss\w+rd&<ä>` is masked in
`{"newPassword":"pa\u0022ss\\w\u002Brd..."}` as well. A form that no serializer produces (a hash, a base64 copy, a truncation) is not
recognised: a handler must not put derived forms of a secret into a message.

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

`context.Request` is the redacted view (sensitive parameter values `"***"`, token `null`): a filter cannot leak what it cannot see, so an audit
filter may serialise it. A filter that passes it (or a copy) to `next` continues with the raw values for the transport; a filter that
edits `ParametersJson` gets its edit used, **merged per member** (AB#7016): a sensitive member that is still `"***"` in the filter's result is
restored from the raw request, so editing, normalizing or enriching a non-sensitive member never sends the placeholder to the handler. A
sensitive member the filter gives another value is an explicit replacement and is used as is; a removed member stays removed. A `"***"` for a
sensitive member that the request does not carry cannot be restored: the call is refused with `METHOD_VALIDATION_FAILED` (`field` = the
member) and the handler is not called. (A filter cannot tell the placeholder from a real value `"***"` of a sensitive parameter; the raw
value wins.) It follows that a filter cannot fingerprint the real parameter values.

The response mirrors this (AB#7007): the filters get the response of the transport with `ResultJson` replaced by `"***"` when the CK result is
`sensitive` (or the method is unknown), and the real response goes back to the caller as long as the filters pass the view through. A
filter that returns a different response (for example a failed one) keeps its change; a filter that answers without calling `next` is returned
as is.

**Sharing facts between filters (AB#7816).** `context.Facts` (`CkMethodInvocationFacts`) is one object per invocation, created by the pipeline:
every filter of that call sees the same instance (also after `context with { Request = ... }`), two calls never share one. An inner filter tells
the outer ones what it did, and the outer one reads it **after `next()` returned**: the idempotency filter (band 300) sets `Facts.ReplayOf` (the
first invocation id of the key) when it answers from its store, and the audit filter (band 100) reads it to write "replay of". Only the idempotency
filter sets `ReplayOf`. `Facts.ParametersFingerprint` (the keyed fingerprint of the raw parameters, an opaque `CkMethodParametersFingerprint`) is
set by the pipeline before the first filter runs and is read-only for filters. The facts are not a member of `CkMethodRequest` or
`CkMethodResponse`, are never serialized and never logged: the `ToString()` of the facts, of the fingerprint and of the context prints no
fingerprint value, and a filter must not log or audit it either.

A filter refuses with a normal failed `CkMethodResponse` (for example `METHOD_FORBIDDEN`), may continue with a modified request
(`next(request with { IdempotencyKey = ... })`) and must not log `ParametersJson` or the token. An exception escaping a filter is
answered `METHOD_HANDLER_FAILED`. The dispatcher of F3.3 runs its bus send as the terminal delegate of the same pipeline
(`pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request, send, ct)`), so authorization and audit see local and remote calls alike.

### Authorization: the declared `authorization` is enforced for every call (F3.4)

The model author declares who may call a method (`authorization: roles / allowSelf / scopes`). A service that dispatches or hosts methods
turns that declaration into a check for every call, bus or in-process, with one line:

```csharp
services.AddCkMethodAuthorization();   // CkMethodAuthorizationFilter at band 200 + the shared evaluator
```

The process that starts the call establishes **who the caller is**. This is never taken from the request that travels over the bus:

```csharp
// gateway: after the token of the HTTP request was validated
using (CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(httpContext.User)))
{
    response = await dispatcher.InvokeAsync(request, cancellationToken);
}
// a job or a pipeline opens the scope with its own identity: new CkMethodPrincipal(subjectOrNull, roles, scopes)
```

| Rule | Meaning |
|------|---------|
| `octo_api` | Always required, even for a method that lists no scope and for administrators. |
| `scopes` | **All** listed scopes are required (compared exactly). |
| `roles` | **Any** listed role suffices (case-insensitive). |
| `allowSelf` | The caller's `sub` equals the target id, compared as parsed object ids (upper-case and lower-case hex are equal). Instance methods only (a static method with `allowSelf` is rejected by the compiler, message 104). |
| default-deny | A method without `authorization`, or with empty `roles`, is for administrators only (`Methods:Authorization:AdministratorRoles`, default `AdminPanelManagement`) and, when `allowSelf` is declared, for the target itself. The owner of the target is refused if nothing is declared. A method that lists roles does not admit administrators implicitly. |
| no caller, unknown method | Refused. |

A refusal is a normal failed response, `METHOD_FORBIDDEN` with `methodId` and `reason = not-authorized`. The filter does not load the target and does
not say which rule failed, so the answer is identical whether the target exists or not. The audit filter (band 100) wraps authorization and records the
refusal; the idempotency filter (300) and the transport never run for a refused call. Handlers still re-validate the forwarded token themselves
(methodConsistency.md rule R9 in octo-documentation): this filter is the second line behind the gateway gate and the only line for callers that do not use the gateway. The evaluator
(`ICkMethodAuthorizationEvaluator`) is a public service, so that the gateway gate uses exactly the same rules without a bus.

### Idempotency store contract (F3.5, AB#7817)

The filter that uses it (band 300) is a later story; this library defines the contract, the lifetime options and an in-memory store
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
  `AddCkMethodInMemoryIdempotencyStore()` (in-memory store unless a store is registered; its state is per process, so tests and single-replica hosts only).

## 8. Duplicate deliveries and failing sends

The bus consumer receives the transport delivery info (`CommandDeliveryInfo`). A **possible duplicate** (the broker redelivers
because the previous consumer lost its connection before the acknowledgement, or the bus retries) is **not executed**: the
previous delivery may have run the method. The caller gets `METHOD_OUTCOME_UNKNOWN` (see the wire contract). The host does not
know which methods are idempotent, so every method is treated as non-idempotent. The invocation id check stays as a second
line (a repeated id is answered `METHOD_OUTCOME_UNKNOWN` as well, also after a refused redelivery). The log line of a refusal names
the invocation id, the method, the tenant and the delivery info (redelivered flag, retry attempt, redelivery count, message id)
and never the parameters or the token.

**One helper for every host (AB#7818, OD-3).** The decision "must this delivery not run again" lives in one public class,
`CkMethodSeenInvocations.Decide(request, delivery)` (namespace `Meshmakers.Octo.Services.Methods.Handlers`), returning
`CkMethodDeliveryDecision.Fresh`, `PossibleDuplicate` (the transport flags the delivery) or `AlreadySeen` (the invocation id is
known). A host that consumes `CkMethodRequest` outside this library (for example the communication controller) must use the
helper instead of keeping its own seen-ids dictionary: it marks the id before it evaluates the flag (a refused redelivery is
remembered), clamps the deadline before it adds the retention (`DateTime.MaxValue` from a foreign sender cannot overflow) and
takes a `TimeProvider` for tests. Keep one instance per host (singleton); the memory is per instance. On `PossibleDuplicate` and
`AlreadySeen` the host answers `METHOD_OUTCOME_UNKNOWN` and does not run the handler; the logging and the refusal counter are the
host's. The helper reads only the invocation id and the deadline and never logs.

**No automatic retry (AB#6907).** The platform never runs a method twice on its own: the dispatcher sends once (no publisher-side
resend; a transport fault ends as `METHOD_TIMEOUT` or `METHOD_HANDLER_NOT_AVAILABLE`), the handler endpoint suppresses faults and the
consumer swallows a failing response send, so the bus retry (AB#4348) cannot run the method again. A handler that throws after a side
effect ran once; the caller gets `METHOD_HANDLER_FAILED`. Tests: a counting handler on the RabbitMQ fixture, and a real broker
redelivery (the connection of the busy instance is closed with `rabbitmqctl close_connection`, the second instance gets the message
flagged and refuses it).

**Limits: what is NOT detected.** There is no "at most once" guarantee.

* The same logical operation with a **new invocation id** (caller retry after a timeout, double click). Helps: the idempotency key
  (F3.5, filter band 300) or `execution.idempotent`.
* The same invocation id on **another instance** without a broker flag (the caller re-sends the request unchanged). The seen-ids
  memory is per instance. A shared store is open decision **OD-3** (`methodConsistency.md`); it is not built here.
* The same invocation id after the handler instance **restarted** (the memory is not persisted).
* A single instance that loses its connection while it works: the queue goes with the consumer, the message is lost and the caller
  runs into `METHOD_TIMEOUT` (outcome unknown).

The seen-ids memory keeps an id until `min(DeadlineUtc, now + 1 h) + 5 min`, capped at `now + 1 h`: a deadline from another sender
(`DateTime.MaxValue`) can no longer overflow the sum.

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

**Timeouts (AB#6906).** The deadline is `request.DeadlineUtc`, the handler's cancellation token fires at it and the handler answers
`METHOD_TIMEOUT` itself. The dispatcher waits `ResponseGrace` longer (`Methods:Dispatcher:ResponseGrace`, default 2 s, the documented
tolerance) before it answers `METHOD_TIMEOUT` itself; message TTL and send timeout are the same value. The rules, in order:

1. **Where the timeout comes from.** `ICkMethodTimeoutResolver` (registered by `AddCkMethodDispatcher`) reads the effective
   `execution.timeoutSeconds` of the method from the CK cache of the tenant (`CkMethodGraph.TimeoutSeconds`: the key, or 15 s without
   it; clamped to 1-300 defensively). `GetDeadlineUtc(tenantId, ckTypeId, methodId)` returns now plus that timeout, clamped to
   `MaxTimeout`; unknown tenant/type/method or a cache fault give the 15 s default and never throw. A gateway builds its request with
   `DeadlineUtc = resolver.GetDeadlineUtc(...)` and `TimeoutSeconds = (int)resolver.GetTimeout(...).TotalSeconds`.
2. **Clamp first.** Before the declaring element is routed, before any filter and before the in-process or bus branch, the dispatcher
   cuts `DeadlineUtc` to `now + Methods:Dispatcher:MaxTimeout` (default 10 minutes; the CK compiler allows 300 s) and fills
   `TimeoutSeconds` (`ceil(remaining)`, clamped to 1-300) when the caller left it out. `DateTime.MaxValue` is therefore served and
   `TimeSpan.MaxValue` (through the resolver) is clamped, not thrown on. A filter that sets a later deadline is cut again before the send.
3. **The handler side.** `request.TimeoutSeconds` is the value of the `timeoutSeconds` parameter of every `METHOD_TIMEOUT` (handler
   host, in-process transport, dispatcher). Without it (another sender) the host falls back to the definition (15 s when the key is
   omitted); AB#6905 shipped with that fallback only, so a method with `timeoutSeconds: 2` reported 15. A request that arrives after its
   deadline is not executed (`METHOD_TIMEOUT`). The host never throws for a deadline: `DateTime.MaxValue` runs the handler (the token
   delay is capped at ~24 days), `DateTime.MinValue` is expired, and the seen-ids retention is computed from `min(DeadlineUtc, now + 1 h)`.
4. **In-process.** The same clamp runs in front of the in-process branch, the handler token fires at the deadline and a handler that
   ignores it is not waited for past `DeadlineUtc` (same envelope as on the bus, tests compare them).

**The registry is advisory.** `ICkMethodHandlerRegistry` (fed by `CkMethodHandlerAnnounced` / `Withdrawn`, expiry = receipt time + the
announced TTL, 45 s by default; a dispatcher asks for a fresh announcement at start) provides the tenant and contract version
checks and diagnostics (`GetLiveHandlers`, `GetLastSeen`, `Snapshot`; the Studio view is F3.2). It never
vetoes a send: an empty registry (dispatcher just started, heartbeat lost) must not refuse a call the broker can still deliver, so the
mandatory flag decides. A withdrawal beats any announcement written before it; a broker "not routable" expires only entries last seen
before the send started.

**The persisted snapshot (AB#5671).** When the host registers `ICkMethodHandlerSnapshotStore` (the MongoDB runtime engine does:
non-CK collection `ck_method_handler_snapshot` in the system database, one document per method and handler instance, unique key plus a
TTL index) the registry is written to it in the background (`Methods:Dispatcher:SnapshotFlushInterval`, default 5 s, one bulk write per
heartbeat round) and loaded by `CkMethodHandlerSnapshotLoadService` (initialization order 28, before the discovery request). A restarted
dispatcher host therefore knows its handlers, the served tenants and the contract versions before the first heartbeat arrives.

* **A row is "last seen", not "live".** A restored row is live only inside its TTL window (`ExpiresUtc`, receipt time + announced TTL),
  exactly like a received announcement. Rows of a stopped handler, an expired TTL or a withdrawal are history: `GetLiveHandlers` does not
  return them, the send is decided by the broker and ends as `METHOD_HANDLER_NOT_AVAILABLE`. A withdrawal is stored as a tombstone with the
  handler's own withdrawal timestamp, so an older announcement that arrives after a restart cannot resurrect the instance.
* **Never more than an announcement could say.** On restore `LastSeenUtc` is capped to now and `ExpiresUtc` to `LastSeenUtc` + 24 h; rows
  last seen more than 24 h ago are skipped. A heartbeat or discovery answer that arrived since is newer and wins over the snapshot row.
* **Service data only.** A row holds ids, service name, versions, times, the served tenant ids and flags; no parameter value, token or user
  data (tests over the stored document and the row type).
* **Optional.** Without the store, or with `Methods:Dispatcher:SnapshotEnabled=false`, nothing is queued and the registry behaves as before.
  A store that is down, or not yet there (system tenant missing), never affects a call: the write is retried with the next flush, a failing
  load is logged and the registry fills from heartbeats.
* Several dispatcher replicas share the collection; an upsert never replaces a row with an older `LastSeenUtc`.

**A handler stopping mid-flight.** A graceful stop withdraws first and removes the endpoints; new calls fail at once with
`METHOD_HANDLER_NOT_AVAILABLE`. A call that is running on that instance gets its cancellation token cancelled and ends as
`METHOD_TIMEOUT` (outcome unknown) or, when the handler finished first, with its result. It is never lost silently and never run twice.
The `timeoutSeconds` of that `METHOD_TIMEOUT` is the timeout of the request (AB#6906), not a host default.

**Tests.** `tests/Methods.Tests/Dispatching` (dispatcher with a faked hub, registry races, declaring-element resolution, registration)
and `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.cs` (private RabbitMQ: the same cases over the bus and in-process,
no consumer, consumer gone, mid-flight stop, timeout, derived type, replicas).

## Replicas and sharing

Several instances of a service (replicaCount > 1) all register the same method endpoint; the broker delivers each call to exactly one of
them. Nothing has to be configured. Proof, a rolling-update caveat (a queue with different arguments blocks the start of the new build,
never the old replicas) and the measured dispatch overhead (p95 about 1 ms on loopback) are in `docs/method-performance.md`.


#### The host enforces the declared authorization (AB#7664)

The handler host does not rely on the calling process: **after** the caller token was validated and **before** the parameters are read, the
host evaluates the `authorization` of the method definition against the caller principal with the same `ICkMethodAuthorizationEvaluator`
as the gateway (no second implementation). This happens for the remote (bus) host and for the in-process host, so a valid tenant token that
is published straight on the method bus cannot reach a handler whose roles, scopes or `allowSelf` it does not meet. The refusal is the
`METHOD_FORBIDDEN` / `not-authorized` envelope of the gateway: it does not name the rule, is identical for an existing and a missing target,
and the handler and the target are not touched. Consequences:

* A registration without a definition (the CK cache has no such method) is refused (default-deny).
* A remote host needs the caller token validation (`AddCkMethodCallerTokenValidation`); without a validated caller every call is refused.
* The in-process host uses the principal of the calling process (`CkMethodPrincipalScope`); a call without one is refused.
* `AddCkMethodHandlers` / `AddCkInProcessMethodHandlers` register the evaluator (`TryAdd`) and the `Methods:Authorization` options
  (`AdministratorRoles`); a service can replace the evaluator before the call.
* A handler need not (and should not) repeat the declared check; it may still apply finer rules on its own data.
