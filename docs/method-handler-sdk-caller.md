---
description: Method handler guide: caller token validation, declared authorization at gateway and host, redaction of sensitive values.
background: true
---

# Method handler: caller identity, authorization and sensitive values

Part of the method handler guide; start at [method-handler-sdk.md](method-handler-sdk.md), which lists all parts.

## Who is the caller? Token validation in a remote handler

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
`AddCkMethodCallerTokenValidation` has no validated caller, so the host refuses every call (`METHOD_FORBIDDEN`, see [The host enforces the declared authorization](#the-host-enforces-the-declared-authorization)). A handler that receives the raw token can replay it
against the APIs until it expires (about an hour by default); an unauthenticated handler announcement on the bus is a separate
decision. Switching the validation off (`Methods:Token:DisableValidation`) is a test configuration; the service refuses to
start outside the `Development` environment. Nothing from the token (value, claims, key id) is ever logged or put into an error.

Unit-test a handler by giving the context a principal: `new CkMethodContext<TParameters>(request, parameters) { CallerPrincipal = new CkMethodPrincipal("user-1", ["AccountManagement"], ["octo_api"]) }`.

## Authorization: the declared `authorization` is enforced for every call (F3.4)

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

`FromClaimsPrincipal` reads `sub` (or the mapped `NameIdentifier`), the roles and the scopes. It maps `client_id` to `ClientId` **only when the
token has no `sub`** (client credentials); for a user token `ClientId` stays `null`, so `Subject` is always the user's `sub`.

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
refusal; the [idempotency filter](method-handler-sdk-idempotency.md#idempotency-filter-at-band-300) (300) and the transport never run for a refused call. Handlers still re-validate the forwarded token themselves
(methodConsistency.md rule R9 in octo-documentation): this filter is the second line behind the gateway gate and the only line for callers that do not use the gateway. The evaluator
(`ICkMethodAuthorizationEvaluator`) is a public service, so that the gateway gate uses exactly the same rules without a bus.

## The host enforces the declared authorization

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

## Logging and sensitive parameters

The host logs method id, invocation id, tenant and correlation id, never `ParametersJson` or the token. Values of parameters
marked `sensitive: true` in the model, and the caller token, are masked (`***`) in an exception message a handler throws
before the host logs it. Do not log `context.Parameters` yourself; the generated records print `***` for sensitive members.

Filters and the audit trail never see those values either: the invocation context hands every filter a **redacted view** of the
request. Sensitive parameter values are `"***"`, the caller token is `null`, a payload that cannot be parsed is `{}`, and a method that is
not known is redacted completely. Only the transport (and so the handler) gets the raw values. A validation error names the parameter
(`field`) and never echoes its value; exceptions logged by the pipeline and the dispatcher are masked the same way.

The same holds for the **result** of a method whose CK result is marked `sensitive`: every filter sees
`ResultJson == "\"***\""` (a JSON string, so it stays valid JSON; a method that is not known is redacted completely), the caller gets the real
result, and the host never writes it into a log line, an exception message, an activity tag or a metric label. `CkMethodResponse.ToString()`
never prints the result. Error responses are not touched. Limits: a record result is masked as a whole, and a method that is implemented as a pipeline
refuses a sensitive result (the pipeline execution stores its output).

The masking of exception text covers every form in which a serializer or a URL can write a value, not only the decoded text:
the plain value, the JSON-escaped forms (System.Text.Json default and relaxed encoder, Newtonsoft.Json: `\"`, `\\`, `\uXXXX` for quote,
plus, ampersand, angle brackets, apostrophe, control characters and non-ASCII characters in upper or lower hex case, escaped slash), the
same once more for JSON embedded in a JSON string, and the percent-encoded form. So a password such as `pa"ss\w+rd&<ä>` is masked in
`{"newPassword":"pa\u0022ss\\w\u002Brd..."}` as well. A form that no serializer produces (a hash, a base64 copy, a truncation) is not
recognised: a handler must not put derived forms of a secret into a message.
