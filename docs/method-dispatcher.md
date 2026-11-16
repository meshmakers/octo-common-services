---
description: CK method dispatcher: routing order, error mapping, timeouts, no retry, handler registry, snapshot and diagnostics.
applies_to: src/Methods/Dispatching/**
---

# CK method dispatcher

### Method dispatcher

`ICkMethodDispatcher` (`src/Methods/Dispatching/`, registration `AddCkMethodDispatcher()` + `AddCkMethodHandlerRegistryFeed()` in the
`AddDistributionEventHub` configuration) is the calling side of a CK method. Guide: `docs/method-handler-sdk-delivery.md`.

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
  duplicate-delivery store is open decision OD-3 (`methodConsistency.md`) and is **not** built; do not add one here. Keyed retries are
  a separate mechanism, the idempotency filter and store (`docs/method-pipeline.md`, `docs/method-wire-contract.md`).
- **Registry** (`ICkMethodHandlerRegistry`, in-memory, **gate of the bus send**): fed by announce/withdraw, expiry = receipt
  time + TTL (dispatcher clock), withdrawal beats older announcements, `MarkNoQueueBound` spares instances announced during the send. No
  live entry = `METHOD_HANDLER_NOT_AVAILABLE` / `no-live-handler` **without a send** (the request carries passwords and the caller token);
  inside `Methods:Dispatcher:RegistryWarmUp` (20 s, restarted by the discovery request) a call for a method the registry has not heard of
  (or knows only from the snapshot) waits for an announcement first. The send stays
  mandatory (crash inside the TTL = one returned send, then expired). Emergency switch `Methods:Dispatcher:RefuseWithoutLiveHandler=false`
  restores the old advisory behaviour. Also used for the tenant check (`tenant-not-served`) and the contract version check.
  `CkMethodDispatcherStartupService` (order 30) publishes a discovery request at start. Diagnostics: see below.
- **Hand-over** (`Methods:Dispatcher:HandlerHandOver`, default 3 s, at most 30 s, zero = off): after a withdrawal, an expiry or a
  returned send (`MarkNoQueueBound`) the registry has an answer, but a successor may be about to announce itself (single-replica
  restart, `Recreate` rollout, scale 0 -> 1, crash with a restart, handler re-binding its queue after a broker reconnect). A call that
  arrives within the hand-over **counted from the moment the last received entry of the method ended** (`GetLastEnded`: newest
  `ExpiresUtc` of the entries with source `Announcement`) waits for an announcement, at most until the window ends or the request deadline,
  then it is sent or refused with `METHOD_HANDLER_NOT_AVAILABLE` / `no-live-handler` exactly as before (same WARN line, same
  `ck.method.calls` / `ck.method.no_handler` counting; nothing is counted for the wait itself). A call after the window is refused at once,
  so a handler that is gone for good adds no latency. A late or repeated withdrawal of an entry that had already ended keeps its end, so
  it does not start a new window. The time the wait used is taken from the deadline the send sees.
  Bounds: the wait holds no thread (one shared `TaskCompletionSource` per registry change plus a `Task.Delay` on the `TimeProvider`),
  follows the caller's token, and at most `Methods:Dispatcher:MaxWaitingCalls` (1000) calls wait in a hand-over at a time, over all
  methods (`CkMethodHandlerRegistry.TryEnterWait`/`LeaveWait`; the warm-up wait after a start is not counted and unchanged); a call beyond
  it is refused at once with a WARN that names the method only. Zero lets no call wait in a hand-over.
- **Discovery after a returned send** (`CkMethodHandlerDiscoveryRequester`, singleton of `AddCkMethodDispatcher`): when the broker returns
  a send the registry still listed, the dispatcher expires the entries (`MarkNoQueueBound`) and publishes a `CkMethodHandlerDiscoveryRequested`,
  so live handlers re-announce within milliseconds instead of with their next heartbeat (up to 15 s). The returned call itself is not sent
  again (no retry); the next calls wait in the hand-over and find the re-announced handler. While a call waits in the hand-over of an entry
  that ended with a returned send, it asks again once per second (a handler that was still reconnecting missed the first request; the
  wait wakes at least every second for it). Fire and forget on the thread pool, at most one request in flight and at most one per second
  per dispatcher, a publish abandoned after 10 s; a failing publish is logged with the exception type only and never changes an answer. A
  withdrawal does not ask: a stopped instance's successor announces itself on start. `MarkNoQueueBound` makes a restored snapshot row
  first-hand (`Source` `Announcement`), so a returned send gets the hand-over also right after a dispatcher restart.
  **A handler that answers but stays unbound** (its process runs, its method queue does not): the waiting calls are sent once when it
  re-announces and returned again. A second return of the same instance within 30 s of the previous one
  (`CkMethodHandlerRegistry.NoQueueBoundRepeatWindow`) is marked repeated: no hand-over wait and no discovery request for it, its calls are
  refused at once until its next heartbeat, as before the hand-over existed. So such a handler costs one extra round of returned sends per
  episode, never a loop of discovery requests.
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
  now references FakeItEasy for a fake `ICkCacheService`). Hand-over and discovery: `CkMethodDispatcherTests` (`HandOver_*`,
  `BrokerReturn_*`, `MaxWaitingCalls*`: single-replica restart, crash expiry, window counted from the end of the entry, deadline and cap,
  cancellation, waiter bound, throttled and repeated discovery, failing publish) and `CkMethodDispatcherBusTests.HandOver.cs` (private
  RabbitMQ: restart of the only replica while a call waits; no successor; the handler's broker connection closed with a heartbeat of
  2 min, the first call after the reconnect succeeds within seconds — 1.4 to 2.3 s measured on a developer machine, never with the
  hand-over off).
