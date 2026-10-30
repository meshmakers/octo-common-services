# CK method wire contract (v1)

Owner: AB#6874 (F3.1). Consumers: the handler SDK (AB#6875), the in-process transport (AB#6876), the pipeline handler
(AB#6879), the dispatcher and queues (F3.3, AB#5691), the registry (F3.2, AB#5690), authorization and audit (F3.4/F3.5)
and operations (F3.6).

Namespace `Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods`
(`src/Contracts/DistributionEventHub/Methods/`, package `Meshmakers.Octo.Services.Contracts`). The types are plain
records without MassTransit attributes. They are serialized by the event hub (System.Text.Json, camelCase, nulls
written, `SystemTextJsonMessageSerializer.Options`). The golden files in `tests/Contracts.Tests/Methods/Golden/` pin the
serialized form of every message; a wire change fails `MethodWireContractTests`.

## Messages

| Message | Direction | Role |
|---|---|---|
| `CkMethodRequest` | dispatcher/gateway -> method queue (`CkMethodQueueNames.For(methodId)`) | One invocation |
| `CkMethodResponse` | handler -> dispatcher (request/response over the hub) | Result envelope; the same for the bus, the in-process transport and the pipeline handler |
| `CkMethodHandlerAnnounced` | handler -> all dispatchers (start, heartbeat, reply to discovery) | Registry feed |
| `CkMethodHandlerWithdrawn` | handler -> all dispatchers (graceful stop) | Registry feed |
| `CkMethodHandlerDiscoveryRequested` | dispatcher -> all handlers (start) | Asks handlers to announce now |

### CkMethodRequest

| Member | Type | Meaning |
|---|---|---|
| `contractVersion` | int | Wire contract version the sender wrote. Missing reads as 1. |
| `tenantId` | string | Tenant the method is invoked in. |
| `invocationId` | Guid | Unique id of this invocation; correlates dispatcher, handler, logs and audit. |
| `correlationId` | Guid? | Optional id of the request that caused the call (GraphQL request, pipeline execution). Copied into handler logs and into the `correlationId` error parameter. |
| `methodId` | string | Qualified, model-version-less method id, e.g. `System.Identity/User.ChangePassword-1`. |
| `ckTypeId` | string | Concrete CK type id of the target entity. |
| `targetRtId` | string? | Runtime id of the target; null for static methods. |
| `parametersJson` | string | Method parameters as camelCase JSON object. **Sensitive**, never log. |
| `caller` | `CkMethodCaller`? | Informational caller identity (`subjectId`, `clientId`, `tenantId`, `name`, `roles`). Handlers must not authorize on it. |
| `callerAccessToken` | string? | Raw JWT for the handler to re-validate. **Sensitive**, never log. |
| `deadlineUtc` | DateTime | Absolute deadline. A handler that receives the request later answers `METHOD_TIMEOUT` without executing. |
| `timeoutSeconds` | int? | Timeout of the method in seconds that `deadlineUtc` was derived from (informational; the deadline decides). The dispatcher fills it when the caller did not. It is reported back as the `timeoutSeconds` parameter of `METHOD_TIMEOUT`. Additive, no version bump. |
| `idempotencyKey` | string? | Client key for safe retries of non-idempotent methods (F3.5; ignored until then). |

`CkMethodRequest` is sealed and overrides `ToString()` to print only method, tenant, target and invocation id; the
members above marked sensitive must never reach a log, an exception message or an error parameter.

### CkMethodResponse (response envelope)

| Member | Type | Meaning |
|---|---|---|
| `contractVersion` | int | Version the answering side wrote. Missing reads as 1. |
| `invocationId` | Guid | The request's invocation id. |
| `success` | bool | True for a result or for acceptance of an asynchronous method. |
| `error` | `CkMethodError`? | Set when `success` is false. |
| `resultJson` | string? | camelCase JSON result of a synchronous method that declares a result. |
| `operation` | `CkMethodOperationHandle`? | Set when an asynchronous method was accepted. |
| `handlerInstanceId` | string | Instance that answered; empty for platform outcomes (no handler answered). |

Exactly one of three shapes:

| Shape | `success` | `error` | `operation` | `resultJson` |
|---|---|---|---|---|
| Succeeded (sync) | true | null | null | result or null |
| Failed | false | set | null | null |
| Accepted (async) | true | null | set | null |

Construct them with `CkMethodResponse.Succeeded`, `.Failed` and `.Accepted`; `IsAccepted` is a convenience that is not
serialized.

**Error model.** `CkMethodError` has a stable `code`, a `parameters` dictionary (string to string) and optional `details`
(`code` + `parameters`). There is no message text on the wire: clients map `code` + `parameters` to text in their own
language. Parameter values never contain method parameter values. A method declares its own codes in its CK model
without the reserved prefix `METHOD_` (`CkMethodErrorCodes.IsValidDeclaredCode`: UPPER_SNAKE_CASE, 3 to 64 characters;
the CK compiler enforces the same, message 103).

**Asynchronous handle.** `CkMethodOperationHandle`: `operationId` (opaque string, unique in the tenant),
`acceptedAtUtc`, `retryAfterSeconds`, `expiresAtUtc`. The polling and cancel surface is F3.6; the handle shape is
frozen here so the answer to an asynchronous call does not change later.

## Platform error codes

Source of truth: `CkMethodErrorCodes` (constants and `Catalog`). The test `ContractDocument_ListsEveryPlatformCode...`
fails when this table lacks a code or a well-known parameter.

| Code | Method ran? | Parameters | Meaning and caller action |
|---|---|---|---|
| `METHOD_HANDLER_NOT_AVAILABLE` | no | `methodId`, `reason` | No live handler (not announced, expired, broker returned the request, pipeline not deployed). Retry later. |
| `METHOD_TIMEOUT` | unknown | `methodId`, `timeoutSeconds` | No answer before the deadline, or the request arrived after it. Do not retry a non-idempotent method blindly. |
| `METHOD_FORBIDDEN` | no | `methodId`, `reason` | Caller may not invoke the method on this target. Do not retry. |
| `METHOD_VALIDATION_FAILED` | no | `methodId`, `field`, `reason` | A parameter is missing or invalid. Fix `field` and call again. |
| `METHOD_TARGET_NOT_FOUND` | no | `methodId` | Target does not exist or is not visible. |
| `METHOD_HANDLER_FAILED` | yes, failed | `methodId`, `correlationId` | Unexpected handler failure; details only in the handler log under `correlationId`. No stack trace on the wire. |
| `METHOD_OUTCOME_UNKNOWN` | unknown | `methodId` | Possible duplicate delivery, not executed again. Check state before calling again. |
| `METHOD_NOT_SUPPORTED_BY_HANDLER_VERSION` | no | `methodId`, `supportedVersion` | Reserved: handler does not support the method version. |
| `METHOD_CONTRACT_VERSION_UNSUPPORTED` | no | `requestedVersion`, `supportedVersion` | Message uses a newer wire contract version than the receiver understands. Wait for the rolling update. |
| `METHOD_PARAMETERS_TOO_LARGE` | no | `methodId`, `maxBytes`, `actualBytes` | The UTF-8 byte length of `ParametersJson` is above the limit of the service (`Methods:MaxParametersBytes`, default 1 MiB = 1048576; exactly the limit passes). The gateway refuses before it validates the parameters, the host re-checks before it deserializes them. Do not retry unchanged. |
| `METHOD_REQUEST_IN_PROGRESS` | no | `methodId` | A call with the same idempotency key (same tenant, method and caller subject) is still running; this call was not executed. Send the same key again later: the first result is then replayed. A claim counts as running until the request deadline plus the response grace of the dispatcher; after that the first outcome is unknown and the key is burned. |

Well-known parameter names (`CkMethodErrorParameters`, camelCase): `methodId`, `correlationId`, `field`, `reason`,
`timeoutSeconds`, `requestedVersion`, `supportedVersion`, `maxBytes`, `actualBytes`. `reason` values are short kebab-case tokens such as
`no-live-handler` or `pipeline-not-deployed`. `METHOD_FORBIDDEN` carries `not-authorized` (method authorization) or, from a remote handler
host that validates the caller token, `token-missing`, `token-expired`, `token-invalid`, `tenant-not-allowed` or `audience-mismatch`
(see `method-handler-sdk.md`, section 4a).

## Rules of evolution

1. **Additive only within a version.** Allowed without a version change: a new optional member with a documented default,
   a new platform error code (plus catalog entry and table row), a new well-known error parameter, a new `reason` token.
2. **Readers ignore unknown members** and treat a missing member as its default. This makes a rolling update safe in both
   directions. Never make a reader fail on an unknown member (do not enable `UnmappedMemberHandling.Disallow`).
3. **Never** remove or rename a member, change its type or meaning, make an optional member required, reuse a code with a
   different meaning, or change the casing/format of a value. These are breaking.
4. **A breaking change increments `CkMethodContract.CurrentVersion`.** A handler announces the highest version it
   understands (`CkMethodHandlerAnnounced.contractVersion`); a dispatcher never sends a newer one to it. A receiver that
   gets a newer message answers `METHOD_CONTRACT_VERSION_UNSUPPORTED` (`CkMethodContract.IsSupported`). Two versions are
   served side by side for one release line before the old one is dropped.
5. **Frozen after release.** From the first release of the 3.5 line the wire format changes only by rule 1. A golden file
   changes only together with a documented additive member; refresh with `OCTO_UPDATE_GOLDEN=1` and review the diff.
6. **Method versions are separate.** The version in the method id (`ChangePassword-1`) versions the method's parameters
   and result and is governed by the CK SemVer rules; it is not the wire contract version.

## Queue names and registry timing

`CkMethodQueueNames.For(id)`: `System.Identity/User.ChangePassword-1` -> `octo::method::system.identity::user::changepassword-1`
(lower-case, model version stripped, keyed by the declaring type, method version mandatory). Announce and heartbeat
defaults: 15 s interval, 45 s TTL (carried in the announcement; final values are decided by F3.2).

### Sending a request

Handlers consume the method queue as a shared (competing-consumer) endpoint. A request is sent with the event hub overload
`GetCommandResponseAsync(CkMethodQueueNames.For(id), request, new CommandSendOptions { Mandatory = true, Timeout, TimeToLive }, ct)`.
`Mandatory` turns "no consumer" into a `CommandNotRoutableException` within milliseconds (`METHOD_HANDLER_NOT_AVAILABLE` in
the dispatcher), and a handler is announced only after its endpoint accepts messages, so an announced handler is routable.
The plain `GetCommandResponseAsync` overload addresses a different (temporary, exclusive) exchange and does not reach a handler.

## Security notes

The bus is internal and not authenticated per message: `caller` is informational and a forged value must have no effect;
authorization re-validates `callerAccessToken` (F3.4). Sensitive members are `parametersJson` and `callerAccessToken`, and `resultJson` of a response whose CK result is marked `sensitive`. The
redacted view of the response exists only inside the calling process (the invocation filters); the envelope on the bus is unchanged.
