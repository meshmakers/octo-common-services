---
description: CK method dispatcher: routing order, error mapping, timeouts, no retry, handler registry, snapshot and diagnostics.
applies_to: src/Methods/Dispatching/**
---

# CK method dispatcher

### Method dispatcher

`ICkMethodDispatcher` (`src/Methods/Dispatching/`, registration `AddCkMethodDispatcher()` + `AddCkMethodHandlerRegistryFeed()` in the
`AddDistributionEventHub` configuration) is the calling side of a CK method. Guide: `docs/method-handler-sdk.md` section 9.

- **Order**: resolve the declaring element (`CkCacheMethodDeclaringElementResolver`: `CkTypeGraph.AllMethods` of the tenant, rewrites
  `MethodId` to the qualified id of the declaring type; inherited and interface methods route to the declaring queue; unknown = as
  given) -> in-process transport when `ICkInProcessMethodTransport.CanHandle` -> otherwise `ICkMethodInvocationPipeline.ExecuteAsync("bus",
  request, send, ct)`. **The dispatcher adds no filter and no pipeline**; `send` is one mandatory, non-persistent
  `GetCommandResponseAsync` (never resent). It never throws for a failing call; only caller cancellation surfaces.
- **Error mapping**: `CommandNotRoutableException` -> `METHOD_HANDLER_NOT_AVAILABLE` (`methodId`, `reason` `no-live-handler`);
  `DistributionTimeoutException` / deadline passed -> `METHOD_TIMEOUT`; `DistributedOperationFailedException`, bus-side cancellation,
  resolver/DI faults -> `METHOD_HANDLER_FAILED` + `correlationId`. No exception text on the wire.
- **Timeout**: `NormalizeTimeout` runs first in `InvokeAsync` (before routing, filters and the in-process/bus branch): it cuts
  `DeadlineUtc` to `now + MaxTimeout` (10 min, also the deadline the handler sees) and fills `CkMethodRequest.TimeoutSeconds`
  (`ceil(remaining)`, 1-300) when the caller left it out. The bus send waits `DeadlineUtc - now + ResponseGrace` (2 s, the documented
  tolerance), TTL the same, and clamps again (a filter may set a later deadline). `ICkMethodTimeoutResolver` (public, `AddCkMethodDispatcher`;
  `CkMethodTimeoutResolver.cs`) is how a gateway gets the deadline: effective `execution.timeoutSeconds` from the CK cache
  (`CkMethodGraph.TimeoutSeconds`, 15 s default, clamped 1-300; unknown method or cache fault = default, never throws). Clamping rules
  live in internal `CkMethodDeadline`. `timeoutSeconds` of every `METHOD_TIMEOUT` is the request's `TimeoutSeconds`.
- **No retry**: one `GetCommandResponseAsync` per call, never resent; every transport fault is `METHOD_TIMEOUT`,
  `METHOD_HANDLER_NOT_AVAILABLE` or `METHOD_HANDLER_FAILED`. Duplicates: see the bus transport in `docs/method-host.md`. Not detected (no "at most once"):
  new invocation id, other instance without broker flag, restarted instance, single instance that loses its connection. A shared
  duplicate store / idempotency store is open decision OD-3/OD-4 (`methodConsistency.md`) and is **not** built; do not add one here.
- **Registry** (`ICkMethodHandlerRegistry`, in-memory, advisory): fed by announce/withdraw, expiry = receipt time + TTL (dispatcher
  clock), withdrawal beats older announcements, `MarkNoQueueBound` spares instances announced during the send. Used for the tenant check
  (`tenant-not-served`) and the contract version check (never send a newer version than announced); it never vetoes a send, the broker's
  mandatory flag decides. `CkMethodDispatcherStartupService` (order 30) publishes a discovery request at start. Diagnostics: see below.
- **Registry snapshot**: with an `ICkMethodHandlerSnapshotStore` in the host (port and Mongo store live in
  `octo-construction-kit-engine-mongodb`, plain collection `ck_method_handler_snapshot` in the system DB, TTL index) the registry queues
  every change (`CkMethodHandlerRegistry.TakeChanges`) and `CkMethodHandlerSnapshotFlushService` writes it every
  `Methods:Dispatcher:SnapshotFlushInterval` (5 s); `CkMethodHandlerSnapshotLoadService` (order 28, before the discovery request) restores
  it (`Restore`). A row is **last seen, not live**: live only inside `ExpiresUtc` (receipt time + TTL), withdrawal = tombstone with the
  source timestamp, restored times are capped (`LastSeenUtc` <= now, expiry <= last seen + 24 h, older than 24 h skipped), received
  announcements win. Rows are service data only (allow-list test). No store or `SnapshotEnabled=false` = in memory as before; a failing
  store never affects a call (requeue, retry next flush). Tests: `tests/Methods.Tests/Dispatching/CkMethodHandlerSnapshotTests.cs`.
- **Registry diagnostics**: `ICkMethodHandlerRegistryDiagnostics` (public, `AddCkMethodDispatcher`; implementation
  `CkMethodHandlerRegistryDiagnosticsService`) returns the dispatcher instance id, every entry with `Source` (`Announcement` | `Snapshot`;
  `Restore` sets `Snapshot`, any received announcement or withdrawal sets `Announcement`, not persisted) and `State`
  (`CkMethodHandlerEntry.GetState(now)`: `Live` | `Expired` | `Withdrawn` | `NoQueueBound`, same rules as `GetLiveHandlers`), the snapshot
  health from `CkMethodHandlerSnapshotPersistence` (`Enabled`, last load time/rows read/restored, last flush success, last flush failed +
  exception **type name only**) and the method ids of `ICkInProcessMethodTransport` (transport `in-process`). Service data only (allow-list
  test). Tests: `tests/Methods.Tests/Dispatching/CkMethodHandlerRegistryDiagnosticsTests.cs`. The GraphQL query lives in asset-repo.
- **Tests**: `tests/Methods.Tests/Dispatching`, `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.cs` (the integration project
  now references FakeItEasy for a fake `ICkCacheService`).
