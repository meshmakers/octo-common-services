---
description: CK method in-process transport and invocation pipeline: detach, filters, authorization, caller token, redaction, fingerprint.
applies_to: src/Methods/InProcess/**, src/Methods/Authorization/**, src/Methods/Redaction/**
---

# CK method invocation pipeline

### In-process transport and invocation pipeline

For methods the calling service owns itself (`Tenant.*`, `Archive.*` of the asset-repo gateway). Files: `src/Methods/InProcess/`,
tests `tests/Methods.Tests/InProcess/`. Guide: `docs/method-handler-sdk-in-process.md`.

- **Registration**: `AddCkInProcessMethodHandlers(m => m.Handle<THandler>(methodId))`, the same typed handler classes as for
  `AddCkMethodHandlers`. In-process registrations live in their own `CkInProcessMethodRegistrations`: no bus queue, no
  announcement, no heartbeat, no event hub needed. A method registered in both places stops the startup
  (`CkInProcessMethodValidationService`, order 15, also validates against the CK cache).
- **Transport** `ICkInProcessMethodTransport` (public): `CanHandle(methodId)` (a dispatcher asks this before its registry lookup),
  `MethodIds`, `InvokeAsync(CkMethodRequest, ct)`. It wraps the same internal `CkMethodHandlerInvoker` as the bus consumer
  (own instance with the in-process registrations), so the envelope is identical (tests compare serialized envelopes for success,
  declared error, exception, deadline, unknown method, contract version, validation). The handler runs in a new scope of the
  root provider (not the caller's HTTP scope) and outside the caller's transaction. A handler that ignores its token is not
  waited for past `DeadlineUtc`: `METHOD_TIMEOUT` (`CkMethodHandlerInvoker.TimeoutFor`).
- **Detached dispatch and tripwire** (OD-8 part 1; `CkDetachedExecution.cs`, `CkInProcessAmbientStateProbe.cs`; tests
  `InProcessIsolationTests`, `CkMethodDispatcherBusTests` in-process cases): `RunAsync` starts the invoker through
  `CkDetachedExecution.Run` = `Task.Run` under `ExecutionContext.SuppressFlow()`, so the handler's flow has an **empty** ambient context
  (an `AsyncLocal` set by the caller, e.g. the asset-repo request session, is not visible, also not after the first `await`; nothing set by the
  handler leaks back). Carried explicitly, nothing else: the trace (`Activity.Current` of the caller is restored in the flow; our
  `ActivitySource` `CkMethodTelemetry.ActivitySourceName` = `Meshmakers.Octo.Methods` starts the handler span `CK method handle` (a grandchild of the caller: caller > `CK method call` > `CK method handle`),
  tags `ck.method.id`, `ck.method.transport`; subscribed in `ObservabilityBuilder`; without a listener the caller's trace still continues),
  `CurrentCulture`/`CurrentUICulture`, the cancellation token with the deadline (argument), and the call-identifying logging scope
  (`MethodId`, `InvocationId`, `CorrelationId`, `TenantId`, opened by the invoker inside the detached flow). The caller's logging scopes,
  principal scope (`CkMethodPrincipalScope`), HTTP context and any other `AsyncLocal` are deliberately dropped. **Tripwire**: a host registers
  `AddCkInProcessAmbientStateProbe<TProbe>()` (`ICkInProcessAmbientStateProbe.IsRequestStateVisible(IServiceProvider handlerScope)`, singleton,
  thread-safe, never throws, yes/no only). `CkMethodHandlerInvoker` asks every probe (internal `ICkMethodEntryGuard`, in-process only; the bus
  consumer passes none) after the new scope exists and before the handler is resolved; a probe that reports state or throws (fail closed)
  fails the call with `METHOD_HANDLER_FAILED` (parameters `methodId`, `correlationId`; no new wire code) and logs ERROR naming the probe
  type and the exception type only. No probe registered = no check. There is deliberately no switch to turn the detach off. Filters of the
  pipeline (audit, authorization) still run in the caller's context, before the detach.
- **Pipeline** `ICkMethodInvocationPipeline` / `ICkMethodInvocationFilter` (public, `AddCkMethodInvocationFilter<T>()`): ordered
  steps around any transport (`CkMethodTransportNames.InProcess` / `Bus`); lower `Order` runs first, bands in
  `CkMethodInvocationFilterOrder` (Audit 100 outermost, Authorization 200, Idempotency 300 innermost; decided: exactly one audit event per invocation incl. refusals and replays). A filter may refuse (return a failed response), pass a
  modified request to `next`, or wrap the call. An exception in a filter becomes `METHOD_HANDLER_FAILED`; caller cancellation
  passes through. The F3.3 dispatcher runs its bus send as the terminal delegate of the same pipeline instance.
- **Method authorization filter** (`src/Methods/Authorization/`, tests `tests/Methods.Tests/Authorization`,
  `tests/Methods.IntegrationTests/CkMethodAuthorizationBusTests.cs`): `AddCkMethodAuthorization()` registers
  `CkMethodAuthorizationFilter` at band 200 through `AddCkMethodInvocationFilter<T>()` (once per service) plus the shared
  `ICkMethodAuthorizationEvaluator` (public, pure: `Evaluate(authorization, kind, targetRtId, principal)`; the gateway gate uses
  the same instance), `ICkMethodPrincipalProvider` (default `CkMethodPrincipalScope`, an `AsyncLocal` the gateway or job opens with
  `CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(user))` around the call; `FromClaimsPrincipal` maps `client_id` only for a
  token without `sub`, tests `CkMethodPrincipalTests`) and `ICkMethodAuthorizationDefinitionSource`
  (CK cache: concrete target type `AllMethods`, then the declaring type). **The principal is never read from `CkMethodRequest.Caller` or the
  forwarded token**; no principal = refused. Rules: `octo_api` always required, then all declared `scopes` (administrators too); `roles` any-of,
  case-insensitive; `allowSelf` compares parsed `OctoObjectId`s (instance methods with a target only; honoured also with empty roles);
  **default-deny**: no `authorization` or empty `roles` admits only `Methods:Authorization:AdministratorRoles` (default `AdminPanelManagement`)
  and, if declared, the target itself; an administrator is not implicitly admitted to a method that lists roles. An unknown method definition
  is refused. A refusal is a normal failed response `METHOD_FORBIDDEN` with `methodId` and the fixed reason `not-authorized`
  (`CkMethodAuthorizationErrors.Forbidden`): the filter never loads the target, so the answer is the same whether it exists or not; the log
  names the rule (`CkMethodDenial`) but no token, parameter or target id. Audit (100) sees the refusal, idempotency (300) and the transport
  never run. Token trust for remote handlers is settled in the next entry. Open decisions that this filter does not settle:
  CK data permissions for method calls (D3:A), OD-9.
- **Caller token validation for remote handlers** (`src/Methods/Authorization/Token/`, tests
  `tests/Methods.Tests/Authorization/Token`): the bus is not authenticated per message, so `CkMethodRequest.Caller` proves nothing. A
  service that opts in with `AddCkMethodCallerTokenValidation(o => o.Authority = <authority of its HTTP API>)` (section `Methods:Token`)
  makes `CkMethodHandlerInvoker` (bus path only) validate `CallerAccessToken` **before the parameters are interpreted** and expose the
  result as `CkMethodContext.CallerPrincipal` (`CkMethodPrincipal` + `ClientId`, `TenantId`, `Subject`, `IsServiceToken`; the three-argument
  constructor stays). `ICkMethodCallerTokenValidator.ValidateAsync(token, tenant, ct)` / `ValidateAsync(request, ct)` is public for hosts
  that handle requests themselves (the pipeline handler) and returns `CkMethodTokenValidationResult` (`Principal` or `Failure` =
  `METHOD_FORBIDDEN` + `reason`). Reasons (`CkMethodTokenReasons`, additive): `token-missing`, `token-expired`, `token-invalid`,
  `tenant-not-allowed`, `audience-mismatch`; a missing `octo_api` scope, a service token without `client_id`, an unknown issuer/key and
  `alg: none` are all `token-invalid` (the answer never names the claim). Settings mirror the HTTP API: `Authority` (issuer = authority with
  trailing slash, keys from `{Authority}.well-known/openid-configuration` JWKS), `ValidateAudience` **off** unless `ValidAudiences` is set
  (then one of them is required; with it empty every `octo_api` token of the tenant is accepted), scope `octo_api`, clock skew 30 s,
  asymmetric algorithms only (HS* and `none` can never be configured in). Tenant rule = `TenantAuthorizationMiddleware`, always enforcing:
  `tenant_id` must equal the request tenant (case-insensitive); `allowed_tenants` is never consulted (it is a selection hint); no `sub` =
  service token, needs `client_id`, accepted for the request tenant or when listed in `TenantAuthorization:CrossTenantServiceClientIds`
  (shared options; the `LogOnly`/`Disabled` modes of the HTTP side are never applied); no parent-tenant rule. `token-expired` is only
  reported for a token that is genuine otherwise (re-validated without the lifetime/audience check; expired + forged = `token-invalid`;
  expired + foreign tenant = `tenant-not-allowed`). Keys: own cache (`TimeProvider`), scheduled refresh `KeyAutomaticRefreshInterval` (12 h)
  keeps the cached keys when the identity service is down, an unknown `kid` forces one refresh throttled by `KeyRefreshInterval` (5 min,
  counted from the last download). No keys at all (identity unreachable, no authority) = `CkMethodTokenKeysUnavailableException` ->
  `METHOD_HANDLER_FAILED` (the caller did nothing wrong), never an accepted token. Nothing from the token (value, claim, key id) reaches a log
  line, exception or failure; logs carry tenant, reason and an internal category only (log capture tests). `DisableValidation` is a test
  configuration: unverified claims, tenant and scope rules still apply, and `IValidateOptions` + `ValidateOnStart` fail the start outside
  `Development`. **In-process**: `CkInProcessMethodTransport` builds its invoker without a validator and hands the principal that the calling
  process established (`ICkMethodPrincipalProvider`, default `CkMethodPrincipalScope`) to the handler as `CallerPrincipal`, read in the
  caller's flow before the detached run. Without the opt-in a remote handler has no validated caller (`CallerPrincipal == null`) and the host refuses every call (next entry).
- **Method host enforces the declared authorization** (finding F-1 of the adversarial suite; `CkMethodHandlerInvoker`, tests `tests/Methods.Tests/Authorization/CkMethodHostAuthorizationTests.cs`, adversarial C16): after the caller token validation (remote) or with the principal of the calling process (in-process) and **before the parameters are deserialized**, the invoker calls `ICkMethodAuthorizationEvaluator.Evaluate(definition.Authorization, definition.Kind, request.TargetRtId, principal)` - the same evaluator type the gateway uses, no copied rule - on every path that reaches a handler. A registration without `Definition` is refused (`UnknownMethod`); a missing principal is refused. A refusal is `METHOD_FORBIDDEN` / `not-authorized` (`CkMethodAuthorizationErrors.Forbidden`), byte-identical to the gateway, the handler and the target are not touched; the log names the denial (`CkMethodDenial`) only. `AddCkMethodHandlers` and `AddCkInProcessMethodHandlers` `TryAdd` the evaluator and bind `Methods:Authorization`; the `CkMethodHandlerInvoker` constructor takes the evaluator as a required argument. Tests that are not about authorization register `AllowAnyCallerEvaluator` (integration) or use the fixtures' administrator principal (`HostFixture.Administrator`). Hosts in other repositories (pipeline handler, adapters through the SDK) pick this up with the library update and must enable `AddCkMethodCallerTokenValidation`.
- **Method audit** (`src/Methods/Audit/`, tests `tests/Methods.Tests/Audit`, doc `docs/method-audit.md`): `AddCkMethodAudit()` registers
  `ICkMethodAuditRecorder` (public; the gateway records its pre-dispatch refusals through it with `Transport = "gateway"`) and `CkMethodAuditFilter`
  at band 100 (outermost). Exactly one `AuditEvent` per invocation id, `Category = Method.Invocation`, metadata `invocationId`, `methodId`, `ckTypeId`, `targetRtId`,
  `callerSubject` (the trusted `ICkMethodPrincipalProvider` principal), `callerName`, `transport`, `handlerInstanceId`, `outcome` (`SUCCESS`, `ACCEPTED`, the error code,
  `CALLER_CANCELLED`), `durationMs`, `replayOf` (first invocation id from `CkMethodInvocationFacts.ReplayOf`; a replay is its own event). **Never** parameters (redacted or not),
  `resultJson`, token, idempotency key, fingerprint or exception text: `CkMethodAuditRecord` has no place for them. An inner exception is recorded once as
  `METHOD_HANDLER_FAILED` and rethrown; caller cancellation once as `CALLER_CANCELLED` and rethrown. A failing sink does not fail the call (logged, counter
  `ck.method.audit.failures`; each write bounded by `Methods:Audit:WriteTimeout`, default 5 s); `Methods:Audit:FailCallOnAuditFailure` / `FailCallOnAuditFailureTenants` make it fail (`METHOD_OUTCOME_UNKNOWN` for an executed call,
  a refusal stays a refusal). `EventRepositoryAuditEventSink` implements `IStrictAuditEventSink` (Infrastructure) so the recorder can see the failure.
- **Audit of calls that end before the pipeline** (`CkMethodDispatcher.AuditAsync`, tests `tests/Methods.Tests/Dispatching/CkMethodDispatcherAuditTests.cs`, doc `docs/method-audit.md`):
  the dispatcher's last-line catch (resolver/DI fault, or an exception that escapes `ICkMethodInvocationPipeline` before its filters ran) answers `METHOD_HANDLER_FAILED`
  **and** records one event through `ICkMethodAuditRecorder` (looked up lazily from the service provider, so no `AddCkMethodAudit()` = no event; transport `bus`, `in-process` once
  the in-process transport was chosen; no handler instance; caller = trusted principal, else `request.Caller`). A cancellation that surfaces before the pipeline is recorded once as
  `CALLER_CANCELLED` and rethrown. One `ck.method.calls` increment: the dispatcher counts a fault **before** the pipeline itself (`CkMethodMetrics.RecordCall`); an exception that
  escaped the pipeline was already counted by its `finally`. The gateway records only exceptions it catches, so it never records a dispatcher answer again; a cancellation inside the
  gateway's target-access check is the gateway's own record (transport `gateway`, asset-repo). A recorder failure never changes the answer.
- `CkMethodRequest` carries `Caller` and `CallerAccessToken` unchanged to the handler; neither reaches a log (`ToString()` redacts,
  the redactor masks the token in exceptions).
- **Redacted request view** (`src/Methods/Redaction/`, tests `tests/Methods.Tests/Redaction`,
  `tests/Methods.IntegrationTests/CkMethodRedactionBusTests.cs`): audit is outermost (band 100) and sees the request before
  authorization, so redaction happens **in front of the pipeline, not as a filter**. `CkMethodInvocationContext.Request` is a *view*:
  the values of the CK `sensitive: true` parameters are replaced by `"***"` (whole value, also nested objects/arrays; names match
  case-insensitively), `CallerAccessToken` is `null`, everything else is as built. An unparsable or non-object payload becomes `{}`;
  a method the definition source does not know (or no source at all) is **fail-closed**: every parameter value is masked. The
  raw request goes only to the transport: when a filter calls `next(r)`, the pipeline puts the raw `ParametersJson`/token back.
  An edited `ParametersJson` is **merged per member** (`CkMethodRequestRedaction.TryRestore`): a sensitive member still
  `"***"` is restored from the raw request, a sensitive member the filter gave another value (or removed) is honoured, and a `"***"`
  for a sensitive member the raw request lacks is refused with `METHOD_VALIDATION_FAILED` (tests `FilterParameterEditTests`). Merge
  was chosen over refusing every edit that touches a sensitive member: it is the least surprising (the filter's intent is kept, the
  secret never travels through the filter) and still fails closed. Consequence for F3.5: a filter cannot hash or compare the real parameter values (an
  idempotency fingerprint over the view would equal for different passwords). The sensitivity comes from
  `ICkMethodSensitivityResolver` (registered by `AddCkMethodInvocationPipeline`; reads `ICkMethodAuthorizationDefinitionSource`, else the CK
  cache). `CkMethodRedactor.For(sensitiveNames|null, request)` masks the same values (plain, JSON-escaped in all common encoder variants, JSON-in-JSON, percent-encoded; `SecretForms`) in exceptions the pipeline and the dispatcher log
  (a bus exception that quotes the payload).
  **Per-invocation facts (`CkMethodInvocationFacts`, `src/Methods/InProcess/CkMethodInvocationFacts.cs`, tests `InvocationFactsTests` and `CkMethodDispatcherTests.Facts_*`)**:
  `CkMethodInvocationContext.Facts` is created by the pipeline once per call and shared by all filters of that call (the `with { Request = r }`
  copy keeps the reference), so an inner filter hands facts to an outer one, read after `next()` returns. Members: `ReplayOf` (Guid?, set only by
  the idempotency filter at band 300 when it answers from the store; the audit filter at 100 reads it) and `ParametersFingerprint` (opaque
  `CkMethodParametersFingerprint`: HMAC bytes plus key id; `internal set`, filled by the pipeline; compared in constant time with
  `Matches`). Never on the wire (not a member of the request or the response), never serialized; `ToString()` of the facts, the fingerprint and the
  context print `[redacted]` instead of the value. A context a test builds by hand starts with its own empty facts.
  **Parameters fingerprint (OD-4 row 2 + G1; `CkMethodParametersFingerprinter` / `ICkMethodParametersFingerprinter` in `src/Methods/InProcess/`, options `CkMethodFingerprintOptions` in `src/Methods/Idempotency/`, tests `ParametersFingerprintTests`)**:
  the pipeline computes it from the **raw** request before the redacted view is built, only for a call that carries `IdempotencyKey` (an unkeyed call computes nothing) and only when the
  fingerprinter is registered (`AddCkMethodIdempotencyOptions()` registers it, so a host without an idempotency store pays nothing and refuses nothing). Input: domain tag, `ckTypeId`, `targetRtId`, canonical parameters
  (members sorted ordinal, duplicate member: last wins, numbers normalized `1` = `1.0` = `1e0`, array order kept, null or empty = `{}`), each part length-prefixed. HMAC-SHA-256 with the
  current key, key id stored with it; `Facts.AlternateParametersFingerprints` holds the same request under the previous key (and, for a method without sensitive parameters, the unkeyed
  `sha256`), `Facts.MatchesStoredFingerprint(stored)` verifies a stored fingerprint of any of them in constant time. No key and a method with sensitive parameters (or an unknown method, fail closed):
  the pipeline inserts a refusal step between authorization (200) and idempotency (300): `METHOD_VALIDATION_FAILED`, field `idempotencyKey`, reason `fingerprint-key-missing`; audit (100) sees it,
  the idempotency filter and the handler do not run. No key and no sensitive parameters: unkeyed SHA-256, key id `sha256`. Configuration `Methods:Idempotency:Fingerprint:Current:{Id,Secret}` and
  `:Previous:{Id,Secret}` (secret = base64 of at least 32 bytes, id `[A-Za-z0-9._-]{1,64}`; malformed or half-set keys fail the start, messages name the setting, never the value; keys are read at start;
  all replicas share them). The key options and the fingerprint never appear in logs, exceptions, `ToString()` or audit (canary test). Ops prerequisite: a Vault + Helm secret per environment (Platform Ops).
  **Redacted response view (`CkMethodResponseRedaction` in `CkMethodSensitivity.cs`)**: the mirror of the request side. When
  the CK result of the method is `sensitive` (`ICkMethodSensitivityResolver.IsResultSensitive`; fail-closed `true` for an unknown method,
  a missing source or a faulting source), the terminal step keeps the raw response and hands the filters a view whose `ResultJson` is
  the JSON string `"***"` (still valid JSON; only a present result is masked, error and accepted responses and `Success`/`Operation`/ids
  are untouched). The outermost step restores the raw `ResultJson` when the response that comes back from the chain still carries the
  placeholder, so the **caller gets the real result**; a filter that returns another response (failed, another result) keeps its
  change, and a filter that answers without calling `next` is returned as is. The view exists only in-process; the wire envelope and
  `ContractVersion` are unchanged. Consequence for F3.5: the idempotency filter refuses a keyed call of a method whose result is sensitive
  (`METHOD_VALIDATION_FAILED`, field `idempotencyKey`, reason `result-sensitive`) and stores nothing, so no masked result is replayed. Not covered: per-attribute masking inside a record result (the marker is whole-result), pipeline
  handlers (they refuse a sensitive result, see the controller), and what a handler itself writes. The `CK method handle` span carries only ids and the
  transport name, never a result. The canary tests listen to all activity sources, so a new source cannot leak a result unnoticed.

- **Adversarial suite (F3.4b exit)**: a repeatable attack suite over the real method path, recorded results and the
  list of findings and accepted limitations in `docs/method-adversarial-suite.md`. Code: `tests/Methods.IntegrationTests/Adversarial/`
  (`CkMethodAdversarialBusTests`, trait `Category=Adversarial`; private RabbitMQ container, a fake identity service, no shared
  stack). Three parties on one broker: a remote handler host with caller token validation, a gateway (dispatcher + authorization
  filter + audit probe) and an attacker that publishes crafted requests straight to the method queue. **One command**:
  `dotnet test tests/Methods.IntegrationTests -c DebugL --filter "Category=Adversarial"` (Docker needed); the run writes the
  result table to `adversarial-results.md` next to the test assembly. A case that documents a gap is pinned by a `C..._Gap_`/
  `_Limitation_` test (current behaviour) plus a skipped test with the expected behaviour; when a gap is closed the pin turns
  red, the skipped test is enabled. The asset-repo half (HTTP token layer, gateway, GraphQL) is in
  `octo-asset-repo-services/tests/**/Adversarial`, see its CLAUDE.md. A new method path (filter, transport, handler host) gets
  its cases added here before it ships.
