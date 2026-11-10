---
description: CK method handler host: typed handlers, outcomes, invoker, bus consumer, life cycle, startup validation, sensitive data.
applies_to: src/Methods/Handlers/**, src/Methods/Typed/**, src/Methods/Configuration/**, src/Methods/Operations/**, src/Methods/Facades/**, src/Methods.MongoDb/**
---

# CK method handler host

## CK method handler SDK (CK v2 Phase 3)

Package `Meshmakers.Octo.Services.Methods` (`src/Methods/`, tests in `tests/Methods.Tests` and `tests/Methods.IntegrationTests`,
the latter against a private RabbitMQ container). It is the **service side** of a CK method: a typed handler on top of the
wire contract (`docs/method-wire-contract.md`), for the remote (bus) transport, plus the in-process transport (`docs/method-pipeline.md`). The dispatcher
and registry (F3.2/F3.3) and the pipeline handler are other stories. Developer guide with a working sample:
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
  **Unacknowledged write (rule R3):** a MongoDB write-concern error (`MongoWriteConcernException`, or a
  `MongoWriteException` / `MongoBulkWriteException` with `WriteConcernError`; e.g. the 30 s `wtimeout` of `w:majority`, code 64)
  or the error label `UnknownTransactionCommitResult`, anywhere in the inner-exception chain (the repositories wrap driver
  exceptions), becomes `METHOD_OUTCOME_UNKNOWN` + `correlationId` instead (`CkMethodWriteOutcome`, now a facade over the
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
  member name only, never the value), result serialization. **Result form**: `CkMethodResultShape` writes only
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
  public `CkMethodSeenInvocations.Decide(request, delivery)` (`Fresh | PossibleDuplicate | AlreadySeen`): every host of method
  handlers uses it instead of its own dictionary; the bus consumer only logs and answers. The seen-ids
  retention is `min(DeadlineUtc, now + 1 h) + 5 min`, capped at `now + 1 h` (an endless deadline from another sender cannot overflow). A failing response send is swallowed (a rethrow would let the bus retry run the method
  again).
- **Life cycle** (all internal, `AddCkMethodHandlers(serviceName, m => m.Handle<THandler>(methodId))`): start order 15 validation,
  20 event hub, 25 endpoints, wait for `WaitUntilReadyAsync` of every endpoint (`Methods:EndpointReadyTimeout`, 30 s; timeout = start
  failure) and only then the announcement, heartbeat hosted service (15 s, TTL 45 s from `CkMethodHandlerOptions`, **final** values; validated at startup by
  `CkMethodHandlerOptionsValidator`: interval >= 1 s, TTL >= 2 x interval and <= 24 h, a violation stops the host; keys `Methods:HeartbeatInterval` /
  `Methods:Ttl`, env `OCTO_Methods__HeartbeatInterval` / `OCTO_Methods__Ttl`), withdraw before
  the endpoints are removed on graceful stop, `AddCkMethodHandlerDiscovery()` in the `AddDistributionEventHub` configuration
  answers dispatcher discovery with an immediate announcement.
- **Startup validation** (`Methods:ValidateRegistrations`, default on): every registration is checked against the CK cache of the
  system tenant (or `Methods:ValidationTenantIds`): unknown method id, a CK parameter without a property on `TParameters`,
  scalar type mismatch, a required property without a CK parameter, result/no-result mismatch. A failure stops the service
  and names the method. The check also records sensitive parameter names and declared error codes for run time.
  Tenant ids (the default `SystemTenantId` like `OctoSystem` and every `ValidationTenantIds` entry) are normalised (trimmed, lower-case) to the
  key the CK cache uses (`CkCacheMethodDefinitionSource.NormalizeTenantId`, tests `CkCacheMethodDefinitionSourceTests`), so a host with the
  default configuration starts; no `ValidationTenantIds` workaround is needed.
- **Sensitive data**: the host never logs `ParametersJson` or the token (`CkMethodRequest.ToString()` redacts, the host logs ids
  only). `CkMethodRedactor` additionally masks the values of CK `sensitive: true` parameters and the caller token in the
  message, stack trace and `ToString()` of an exception a handler throws before the host logs it. Tests: `CkMethodHandlerInvokerTests`.
  **Sensitive result**: `CkMethodResponse.ToString()` never prints `ResultJson` (ids, success, error code, operation id and
  `hasResult` only); the host never puts the result into a log line, an exception message, an activity tag or a metric label. A
  failure while the host *serializes* the result (`CkMethodResultSerializationException`, thrown by the adapter) is logged with its
  inner exception only for a non-sensitive result; for a CK result marked `sensitive` (`CkMethodRegistration.SensitiveResult`) only the
  fault type is logged. A failing `respond` of the bus consumer is logged through `CkMethodRedactor.WithResult(resultJson)` (masks every
  value of the result, plain and escaped) when the result is sensitive or the method is not registered. Tests: `RedactedResponseViewTests`.
- **Event hub requirement**: the endpoint readiness, delivery info, fault suppression and mandatory send come from
  `Meshmakers.Octo.Common.DistributionEventHub` (opt-in primitives, see `octo-distributedEventHub/CLAUDE.md` "Shared Command Endpoints"). This repo
  needs the event hub release that contains them. The migration of the older exclusive command queues is a separate story.

### REST facades: self-hosted methods run in-process (F5.1)

A service calls a method it hosts itself (REST facade, first consumer: identity) through `ICkMethodDispatcher`;
the handler runs in this process, nothing is sent to the broker. Code: `src/Methods/InProcess/` (transport), `src/Methods/Configuration/`
(registration), `src/Methods/Facades/CkMethodFacadeResults.cs`. Guide: `docs/method-handler-sdk.md` section 10.

- **Bus handlers are local for their own host.** `AddCkMethodHandlers` also registers the local transport (`AddCkLocalMethodTransport`,
  internal, idempotent). `CkInProcessMethodTransport` takes the bus registrations (`CkMethodHandlerRegistrations`) and the announced
  `CkMethodHandlerInstance` as optional constructor arguments and builds a second `CkMethodHandlerInvoker` over them (same evaluator,
  in-process entry guard, no token validator). `CanHandle`/`MethodIds` cover both sets; the in-process registrations win. The envelope
  carries the announced instance id, so it is byte-identical to the bus answer of the same host. Behaviour change for hosts that have
  both `AddCkMethodHandlers` and `AddCkMethodDispatcher`: their own calls of a hosted method no longer go over the bus (no such host on
  main at the time; asset-repo hosts in-process handlers only).
- **Local only.** `ICkMethodHandlerRegistrationBuilder.HandleLocalOnly<T>(id)` (new interface member; nothing outside this repo implements
  the builder). In `AddCkMethodHandlers` it adds to `CkInProcessMethodRegistrations`: not announced, no queue, another process gets
  `METHOD_HANDLER_NOT_AVAILABLE`. In `AddCkInProcessMethodHandlers` it equals `Handle`. The one-place rule is the existing check of
  `CkInProcessMethodValidationService` (it now returns early when there is no in-process registration).
- **Principal.** The local path reads `ICkMethodPrincipalProvider` (default `CkMethodPrincipalScope`) in the caller's flow; no scope =
  `null` principal = `METHOD_FORBIDDEN`, never a system identity.
- **Facade helper.** `CkMethodFacadeResults.ToActionResult(response, CkMethodFacadeOptions?)`: `DefaultStatusCodes` per platform code
  (403/404/400/413/409/504/503/500, a test checks that every `CkMethodErrorCodes.Catalog` entry has one), unknown `METHOD_*` 500, declared
  code 400, success 200 JSON / 204, accepted 202 with the operation handle; body problem details with `code`, `parameters`, `details`. Overrides `MapError(code, status|factory)`,
  `MapSuccess(factory)`.
- Tests: `tests/Methods.Tests/InProcess/SelfHostedMethodCallTests.cs`, `tests/Methods.Tests/Facades/CkMethodFacadeResultsTests.cs`,
  `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.SelfHosted.cs`.

### Asynchronous methods: durable operation record (F3.6, OD-5)

Code: `src/Methods/Operations/` (contract, in-memory store, runner, options, registration) and the new package
`Meshmakers.Octo.Services.Methods.MongoDb` (`src/Methods.MongoDb/`, MongoDB store). Guide: `docs/method-handler-sdk.md` section 11.
Decision OD-5: at most once after accept, record before the accepted envelope, lost handler = `METHOD_OUTCOME_UNKNOWN`,
7 d retention after completion (1 h to 30 d), owner + tenant admin read, no cancel, repeated idempotency key = same operation id.

- **Handler SDK**: `CkMethodOutcome.Accepted(work)` / `CkMethodOutcome<TResult>.Accepted(work)` (static factories on the sealed outcome
  classes; no member was added to `ICkMethodHandler<,>`, so the change is additive for other repositories, §4a). The adapter wraps the work
  (`CkMethodAdapterOutcome.AcceptedWork`). `CkMethodHandlerInvoker` refuses `Accepted` with `METHOD_HANDLER_FAILED` when the definition is not
  `execution.mode: async` (`CkMethodModes.Async`) or the host lacks `AddCkMethodOperations()` (marker `CkMethodOperationHost`, resolved from the
  handler scope, so the in-process and the bus invoker need no constructor change); otherwise it hands the **DI scope** to
  `CkMethodOperationRunner.AcceptAsync` (the invoker no longer disposes it on that path).
- **Runner** (`CkMethodOperationRunner`, internal singleton): writes the record (`CreateAsync`) before it returns the accepted envelope
  (`operationId` = `Guid` "N", `acceptedAtUtc`, `retryAfterSeconds`; `expiresAtUtc` stays empty until completion); store failure =
  `METHOD_HANDLER_NOT_AVAILABLE` / `operation-store-unavailable`, work not started; after `StopAsync` began nothing is recorded,
  answer `METHOD_HANDLER_NOT_AVAILABLE` / `host-stopping`. The work runs once under `ExecutionContext.SuppressFlow()`
  (OD-8), `MarkRunningAsync` first (false = the sweep was faster, the work is not run), outcome mapping as the invoker (declared failure
  through `DeclaredFailure`, exceptions through the redactor, `CkMethodWriteOutcome`), host stop = cancelled = `handler-lost`. A sensitive
  result is never stored. `CkMethodOperationHeartbeatService` (heartbeat of own operations, `StopAsync` stops the work) and
  `CkMethodOperationSweepService` (`FailLostAsync(now - StaleAfter)`); `AddCkMethodOperations()` = runner + heartbeat + sweep,
  `AddCkMethodOperationSweep()` = sweep only (readers such as the gateway).
- **Store contract** `ICkMethodOperationStore`: `CreateAsync`, `GetAsync(tenant, id)` (null for unknown/expired/foreign), `ListAsync(CkMethodOperationQuery)`
  (tenant required; `Owner` = subject, or client id of a record without subject; `MethodId`, `State`, `SinceUtc`; `Skip`/`Take` 1-200; newest
  first; `TotalCount`), `MarkRunningAsync`, `ReportProgressAsync`, `HeartbeatAsync`, `CompleteAsync` (open records only, first final state wins),
  `FailLostAsync`. Tenants are stored lower-case (`CkMethodOperationTenant.Normalize`). Expired records are invisible before the TTL monitor
  removes them. Fails closed with `CkMethodOperationStoreException`. Implementations: `CkMethodInMemoryOperationStore` (tests),
  `CkMethodMongoOperationStore` (collection `MethodOperations` in `OctoSystemConfiguration.SystemDatabaseName` **lower-case**, as the engine's `SystemContext` names it - MongoDB refuses a name that differs from the existing database only in case, so the configured `OctoSystem` failed every call with "db already exists with different case"; `SystemDatabaseName(...)`, test `MongoOperationStoreSystemDatabaseTests`; own admin client with majority
  read/write concern, indexes `tenantId_operationId_unique`, `expiresAt_ttl` (0 s), `tenantId_acceptedAt`, `state_heartbeatAt`; progress and
  error stored as JSON strings, so parameter names never become field names). `Methods.csproj` still references no `MongoDB.*`
  (`CkMethodContextSessionFreeTests`); the driver lives only in `Methods.MongoDb`.
- **Options** `CkMethodOperationOptions` (`Methods:Operations`): `Retention` (7 d, 1 h-30 d), `HeartbeatInterval` (15 s), `StaleAfter`
  (2 min, >= 3 x heartbeat), `SweepInterval` (1 min), `RetryAfterSeconds` (5, 1-3600); `CkMethodOperationOptionsValidator` + `ValidateOnStart`,
  messages name the setting only.
- **Metric** `ck.method.operations` (labels `method_id`, `state`); sweep-ended operations count as `failed` with `method_id` `other`.
- **Tests**: `tests/Methods.Tests/Operations/` (`OperationStoreContract` is linked into `Methods.IntegrationTests`, which runs it against a
  MongoDB replica set via Testcontainers; skipped without Docker or with `OCTO_SKIP_CONTAINER_TESTS=true`), `AsyncHandlerTests`,
  `AsyncIdempotencyTests`, `OperationOptionsTests`; `tests/Methods.IntegrationTests/Operations/MongoOperationStoreTests.cs`.
- **Open for the next stories**: the asset repository reads through `GetAsync` / `ListAsync`, decides owner-or-administrator access and maps a
  `null` to `OPERATION_NOT_FOUND`; it should register `AddCkMethodMongoOperationStore()` + `AddCkMethodOperationSweep()`. The bot services
  (bot-services) registers the store + `AddCkMethodOperations()` and returns `Accepted` from the dump/import handlers.
