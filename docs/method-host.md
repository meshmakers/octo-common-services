---
description: CK method handler host: typed handlers, outcomes, invoker, bus consumer, life cycle, startup validation, sensitive data.
applies_to: src/Methods/Handlers/**, src/Methods/Typed/**, src/Methods/Configuration/**
---

# CK method handler host

## CK method handler SDK (CK v2 Phase 3, AB#6875)

Package `Meshmakers.Octo.Services.Methods` (`src/Methods/`, tests in `tests/Methods.Tests` and `tests/Methods.IntegrationTests`,
the latter against a private RabbitMQ container). It is the **service side** of a CK method: a typed handler on top of the
wire contract above, for the remote (bus) transport, plus the in-process transport (AB#6876, section below). The dispatcher
and registry (F3.2/F3.3) and the pipeline handler (AB#6879) are other stories. Developer guide with a working sample:
`docs/method-handler-sdk.md`.

- **Typed handlers**: `ICkMethodHandler<TParameters, TResult>` (method with a result) or `ICkMethodHandler<TParameters>`
  (no result), `HandleAsync(CkMethodContext<TParameters>, CancellationToken)`. `TParameters`/`TResult` are the generated
  `{Type}{Method}Parameters` / `{Type}{Method}Result` records. A handler is resolved from a **new DI scope per invocation**,
  so it can take scoped services. `CkMethodContext` has caller, tenant, target, correlation, deadline/`RemainingTime`,
  access token; **no database session and no transaction** (methods run outside the request transaction). `CkMethodContextSessionFreeTests` (reflection over the context, handler
  abstraction, outcome and wire records) fails if any reachable member is a session or transaction type or a `MongoDB.*` type;
  `MethodFailureIsolationTests` pins rule 2 (a failing method call leaves earlier committed writes untouched, and a failing
  earlier write does not block an independent call).
- **Outcome and errors**: return the result (implicit conversion), `CkMethodOutcome.Ok()`/`Fail(code, params)` or the platform
  helpers (`ValidationFailed`, `TargetNotFound`, `Forbidden`), or throw `CkMethodException`. `CkMethodFailure` validates the
  code (declared shape or platform code). With CK validation on, a code the method does not declare becomes
  `METHOD_HANDLER_FAILED` (handler bug). Any other exception becomes `METHOD_HANDLER_FAILED` with a `correlationId`
  parameter (request correlation id, else the invocation id); the exception is logged with that id, never sent.
  **Unacknowledged write (AB#8020, rule R3):** a MongoDB write-concern error (`MongoWriteConcernException`, or a
  `MongoWriteException` / `MongoBulkWriteException` with `WriteConcernError`; e.g. the 30 s `wtimeout` of `w:majority`, code 64)
  or the error label `UnknownTransactionCommitResult`, anywhere in the inner-exception chain (the repositories wrap driver
  exceptions), becomes `METHOD_OUTCOME_UNKNOWN` + `correlationId` instead (`CkMethodWriteOutcome`, since AB#8037 a facade over the
  engine's `UnacknowledgedWriteClassifier` in `Runtime.Contracts`, which the mesh adapter and the SDK adapters use for pipeline
  methods too (`PipelineExecutionFailureKind.OutcomeUnknown`); same in the pipeline's
  filter catch and the audit filter, so answer, audit outcome and `ck.method.calls` label agree). The SDK recognises the
  driver types by name and public members: `Methods.csproj` must not reference `MongoDB.*`
  (`CkMethodContextSessionFreeTests`). Tests: `CkMethodWriteOutcomeTests` (real driver exceptions, `MongoWriteFaults`).
- **Host core** `ICkMethodHandlerInvoker` (public, transport independent): contract version check
  (`METHOD_CONTRACT_VERSION_UNSUPPORTED`), unknown method (`METHOD_HANDLER_NOT_AVAILABLE`, reason `method-not-registered`),
  deadline already passed (`METHOD_TIMEOUT`, not executed), deadline cancellation of the handler token (`METHOD_TIMEOUT`; `timeoutSeconds` =
  `request.TimeoutSeconds`, the definition's value only as fallback),
  parameter deserialization with `CkMethodJson.Options` (invalid or missing member -> `METHOD_VALIDATION_FAILED` with the
  member name only, never the value), result serialization. **Result form (AB#8079)**: `CkMethodResultShape` writes only
  `Value` of the generated `{Type}{Method}Result` as the bare JSON value (`"abc"`, `42`, `{...}`), never `{"value": ...}`; a null
  `Value` = no `resultJson`. A result type without a public `Value` fails the registration, a `Value` whose scalar type does not
  fit the declared result fails the startup validation. Tests: `CkMethodResultWireFormTests` (String, Int, Boolean, Enum, Record,
  StringArray, null). Idempotency records stored before the fix replay the old wrapper until their TTL (no migration). It never throws for a request; only the caller's own
  `CancellationToken` surfaces as `OperationCanceledException`. Other transports call this instead of re-implementing it.
- **Bus transport** (`CkMethodBusConsumer`, internal): one shared command endpoint per method (`CkMethodQueueNames.For`, registered
  with `RegisterCommandConsumer(name, handler, CommandConsumerOptions { SuppressFaults = true })`; senders use
  `GetCommandResponseAsync(name, request, CommandSendOptions { Mandatory = true, ... })`, a missing consumer fails with
  `CommandNotRoutableException`). A possible duplicate delivery (`CommandDeliveryInfo.IsPossibleDuplicate`: broker redelivery or
  retry) is not executed and answered `METHOD_OUTCOME_UNKNOWN` (all methods count as non-idempotent; the log line names invocation id,
  tenant and delivery info, never parameters); a repeated invocation id is the second line, with the same answer. The decision is the
  public `CkMethodSeenInvocations.Decide(request, delivery)` (`Fresh | PossibleDuplicate | AlreadySeen`, AB#7818): every host of method
  handlers uses it instead of its own dictionary; the bus consumer only logs and answers. The seen-ids
  retention is `min(DeadlineUtc, now + 1 h) + 5 min`, capped at `now + 1 h` (an endless deadline from another sender cannot overflow). A failing response send is swallowed (a rethrow would let the bus retry run the method
  again, AB#4348).
- **Life cycle** (all internal, `AddCkMethodHandlers(serviceName, m => m.Handle<THandler>(methodId))`): start order 15 validation,
  20 event hub, 25 endpoints, wait for `WaitUntilReadyAsync` of every endpoint (`Methods:EndpointReadyTimeout`, 30 s; timeout = start
  failure) and only then the announcement, heartbeat hosted service (15 s, TTL 45 s from `CkMethodHandlerOptions`, **final** values, AB#7838; validated at startup by
  `CkMethodHandlerOptionsValidator`: interval >= 1 s, TTL >= 2 x interval and <= 24 h, a violation stops the host; keys `Methods:HeartbeatInterval` /
  `Methods:Ttl`, env `OCTO_Methods__HeartbeatInterval` / `OCTO_Methods__Ttl`), withdraw before
  the endpoints are removed on graceful stop, `AddCkMethodHandlerDiscovery()` in the `AddDistributionEventHub` configuration
  answers dispatcher discovery with an immediate announcement.
- **Startup validation** (`Methods:ValidateRegistrations`, default on): every registration is checked against the CK cache of the
  system tenant (or `Methods:ValidationTenantIds`): unknown method id, a CK parameter without a property on `TParameters`,
  scalar type mismatch, a required property without a CK parameter, result/no-result mismatch. A failure stops the service
  and names the method. The check also records sensitive parameter names and declared error codes for run time.
  Tenant ids (the default `SystemTenantId` like `OctoSystem` and every `ValidationTenantIds` entry) are normalised (trimmed, lower-case) to the
  key the CK cache uses (`CkCacheMethodDefinitionSource.NormalizeTenantId`, AB#7912; tests `CkCacheMethodDefinitionSourceTests`), so a host with the
  default configuration starts; no `ValidationTenantIds` workaround is needed.
- **Sensitive data**: the host never logs `ParametersJson` or the token (`CkMethodRequest.ToString()` redacts, the host logs ids
  only). `CkMethodRedactor` additionally masks the values of CK `sensitive: true` parameters and the caller token in the
  message, stack trace and `ToString()` of an exception a handler throws before the host logs it. Tests: `CkMethodHandlerInvokerTests`.
  **Sensitive result (AB#7007)**: `CkMethodResponse.ToString()` never prints `ResultJson` (ids, success, error code, operation id and
  `hasResult` only); the host never puts the result into a log line, an exception message, an activity tag or a metric label. A
  failure while the host *serializes* the result (`CkMethodResultSerializationException`, thrown by the adapter) is logged with its
  inner exception only for a non-sensitive result; for a CK result marked `sensitive` (`CkMethodRegistration.SensitiveResult`) only the
  fault type is logged. A failing `respond` of the bus consumer is logged through `CkMethodRedactor.WithResult(resultJson)` (masks every
  value of the result, plain and escaped) when the result is sensitive or the method is not registered. Tests: `RedactedResponseViewTests`.
- **Event hub requirement (AB#6891)**: the endpoint readiness, delivery info, fault suppression and mandatory send come from
  `Meshmakers.Octo.Common.DistributionEventHub` (opt-in primitives, see its `CLAUDE.md` "Shared Command Endpoints"). This repo
  needs the event hub release that contains them. The migration of the older exclusive command queues is a separate story.
