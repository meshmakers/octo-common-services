# Changelog

All notable changes to the `Meshmakers.Octo.Services.*` packages. Versions come from the pipeline (AB#6291), so
entries are grouped by work item until a release assigns the version.

## Unreleased

### Fixed

- **AB#8876** — `AuditEventAggregationFlushService` resolves the engine `AggregatingAuditEventSink` once in `StartAsync`
  instead of in `StopAsync`. A host that stops after its service provider was disposed (`WebApplicationFactory.Dispose`
  in integration tests, shutdown races) threw `ObjectDisposedException` out of `Host.StopAsync`; `StopAsync` now never
  touches the provider and logs an `ObjectDisposedException` during the flush at debug level. The shutdown-token bound is
  unchanged.

### Added

- **AB#7279** — `AddObservability()` registers the engine meter `Meshmakers.Octo.DataPermissions`
  (`octo.datapermissions.audit.violations`, tags `tenant`, `subject`, `type`, `action`): every AuditOnly data-permission
  violation is counted, while the audit log and the event store now only carry the first occurrence and one summary per
  window per key (engine `AggregatingAuditEventSink`, option `AuditEventAggregation:Window`, default 15 min).
  `AddOctoNotification()` adds `AuditEventAggregationFlushService`, which publishes the pending summaries when the host
  stops, while `EventRepositoryAuditEventSink` can still write to the event store. `Notifications` now references
  `Meshmakers.Octo.Runtime.Engine`.
- **AB#8711** — Every in-process CK method call checks the caller tenant once, centrally: `CkMethodCallerTenantFilter` (band
  `CkMethodInvocationFilterOrder.CallerTenant` = 150, registered with the local transport, always on) answers a call whose caller
  token was issued for another tenant `METHOD_FORBIDDEN` with `reason = tenant-not-allowed` before authorization, idempotency and
  the handler, independent of the HTTP tenant gate's `LogOnly` mode. Same rule as the bus host (`CkMethodCallerTenantRule`, now
  shared with `CkMethodCallerTokenValidator`; clients on `TenantAuthorization:CrossTenantServiceClientIds` pass). New init-only
  properties `CkMethodPrincipal.TokenTenantId` and `IsFromAccessToken`, set by `FromClaimsPrincipal`. Bus path unchanged.
  Consumers: a test principal for an in-process call needs a `tenant_id` claim of the route tenant.
- **AB#8705** — `ImportRtCommandRequest.InitiatedByPrincipalKind` (`RtPrincipalKind`, default `Unknown`): the producer (asset
  repository) sends whether the caller of an RT import is a signed-in person (`Interactive`) or a service client (`Client`), and
  the consumer (bot services) passes it to the engine's `RtImportCallerContext`, where CK types with a `writeAccess` block
  (AB#8574) admit or refuse the import by it. Optional and additive on the wire: a message of an older producer reads as
  `Unknown` (refused only by restricted types, fail closed), an older consumer ignores the field.

- **AB#8443** — asynchronous CK methods keep a durable operation record (F3.6, OD-5). A handler returns
  `CkMethodOutcome.Accepted(work)` / `CkMethodOutcome<TResult>.Accepted(work)` with an `ICkMethodOperationProgress` reporter;
  the host writes the record before the accepted envelope (`operationId`, `acceptedAtUtc`, `retryAfterSeconds`), runs the work
  once, stores progress, heartbeats and the final state, and a sweep ends operations of a lost handler as `METHOD_OUTCOME_UNKNOWN`
  (`reason` `handler-lost`). Store contract `ICkMethodOperationStore` (read by tenant and operation id, paged list by tenant with
  owner, method, state and since filters), `CkMethodInMemoryOperationStore`, and the new package
  `Meshmakers.Octo.Services.Methods.MongoDb` with `CkMethodMongoOperationStore` (collection `MethodOperations` in the system
  database, unique `tenantId + operationId`, TTL on `expiresAt`). Registration `AddCkMethodOperations()`,
  `AddCkMethodOperationSweep()`, `AddCkMethodMongoOperationStore()`, `AddCkMethodInMemoryOperationStore()`; settings
  `Methods:Operations` (`Retention` 7 d, 1 h to 30 d, validated at start). Counter `ck.method.operations` (`method_id`, `state`).
  Additive: no member was added to an interface another repository implements; synchronous handlers are unchanged.

- **AB#8529** — tenant role policies can carry a stable error code:
  `options.AddTenantRolePolicy(policyName, roleName, errorCode)` (new overload; the two-argument form is unchanged). A refusal of
  such a policy is a `403` with `{"message": "...", "code": "<errorCode>"}` instead of `{"message": "..."}`, for the missing scope
  and the missing role alike, so clients (SDK `ServiceProblem`, MCP tool results, Studio) can react to the code. New
  `TenantRoleRequirement.ErrorCode` and `TenantRoleAuthorizationFailureReason` (carries the code). First user: the asset
  repository's `DataModelManagementPolicy` (code `DataModelManagementRequired`). Additive and binary compatible.

- **AB#8415** — `[OctoRestDeprecated(Wave = "<name>", Link = …)]`: the dates of a deprecation wave come from the
  configuration section `RestDeprecation:Waves:<name>` (`Since`, `Sunset`, both optional; in the cluster
  `OCTO_RestDeprecation__Waves__<name>__Since|Sunset` from Helm values) instead of the code, because they are only known
  at rollout. A date that is not configured leaves its header out (`Deprecation` without `Since`, `Sunset` without
  `Sunset`); `Link`, OpenAPI `deprecated: true` (notice `Deprecated, use <link>.` without a sunset date) and the counter
  apply from the first deployment on. An invalid wave name, `Since`/`Sunset` on a wave attribute, or a malformed or
  out-of-order configured date fails the service at startup. Attributes without `Wave` behave as before.

- **AB#8421** — new package `Meshmakers.Octo.Services.RestDeprecation`: `[OctoRestDeprecated(Since, Sunset, Link)]`
  marks a REST action (or controller) deprecated. Every response of a marked endpoint, 4xx/5xx included, carries
  `Deprecation: @<unix seconds>` (RFC 9745), `Sunset: <IMF-fixdate>` (RFC 8594) and
  `Link: <url>; rel="deprecation"; type="text/html"` (plus `Link: <url>; rel="successor-version"`, RFC 5829, with the
  optional `Successor`); the OpenAPI operation is `deprecated: true` with the sunset date
  and link in its description; the counter `octo.rest.deprecated.calls` (meter `Meshmakers.Octo.RestDeprecation`,
  labels `endpoint`, `client_id`, `status_class`; pipeline service accounts `octo-pipeline-sa-*` reported as
  `octo-pipeline-sa`, never with the entity id) counts the remaining calls. Invalid attribute values fail the service
  at startup, naming the action. `AddObservability()` registers the mechanism and exports the meter;
  `AddOctoApiVersioningAndDocumentation()` adds the OpenAPI transformer. Unmarked endpoints are unchanged. See
  `docs/rest-deprecation.md`.

### Fixed

- **AB#8677** — a service no longer stops on start because another service upgrades a CK model in place at the same moment. The
  system tenant setup (`DefaultConfigurationInitializationService`) retries a failure the CK engine marked as "a CK model is being
  imported right now" (`Exception.Data` key `Meshmakers.Octo.CkModelImportInProgress`, set by `octo-construction-kit-engine-mongodb`),
  every 5 s within a total deadline of 5 minutes (one attempt can itself wait up to about 2 minutes in the engine, so the bound is
  time, not an attempt count), also when the mark sits inside an `AggregateException`. A timeout while waiting for the engine's CK
  model import lock (another service imports the model longer than the engine waits, about 90 s) is retried the same way; it is
  recognised exactly — a `TimeoutException` from `Meshmakers.Octo.Runtime.Engine.MongoDb` for a lock `ck_model_import_<model>` — and
  every other `TimeoutException` still fails the host start. After the deadline the original error fails
  the host start, and a stopping host ends the wait at once. Every other failure still fails the host start at once. Seen 2026-11-09 on the local stack:
  System.Bot 3.5.0 → 3.6.0 by BotServices while CommunicationController (embedded import of System.Communication) and PlatformServices
  (CK cache load) started; both hosts stopped. Child tenants were already non-fatal (AB#4690).

- **AB#8634** — security: a CK method call without a live handler is refused **before anything is sent to the broker**. The
  dispatcher (`AddCkMethodDispatcher`) now treats the handler registry as the gate of the bus send: no live entry for the method =
  `METHOD_HANDLER_NOT_AVAILABLE` / `no-live-handler` (same envelope, WARN line and `ck.method.calls` / `ck.method.no_handler`
  counting as before) and no call on the event hub. Before, one mandatory, non-persistent send carried the request, with its
  parameters (identity passwords) and the caller's token, to RabbitMQ, which returned it unroutable, and MassTransit logged
  `ERROR S-FAULT ... CkMethodRequest`. Generic for every method. New options `Methods:Dispatcher:RegistryWarmUp` (default 20 s: a
  freshly started dispatcher waits for announcements of methods it has not heard of yet) and
  `Methods:Dispatcher:RefuseWithoutLiveHandler` (default `true`; `false` = old advisory behaviour, emergency switch only). The send
  stays mandatory for a handler that crashed inside its TTL. No interface or contract change.

- **AB#8415** — `docs/rest-deprecation.md`, the XML docs and the tests describe the pipeline service account client
  ids correctly: `octo-pipeline-sa-{adapterRtId}` for an adapter's account, `octo-pipeline-sa-{configurationRtId}` for a
  standalone account (or a custom client id outside the prefix, reported as itself), not `octo-pipeline-sa-<tenant>`.
