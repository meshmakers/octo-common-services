---
description: Method handler guide: duplicate deliveries and failing sends, the dispatcher (routing, outcomes, timeouts, registry) and replicas.
background: true
---

# Method handler: delivery semantics and the dispatcher

Part of the method handler guide; start at [method-handler-sdk.md](method-handler-sdk.md), which lists all parts.

## Duplicate deliveries and failing sends

The bus consumer receives the transport delivery info (`CommandDeliveryInfo`). A **possible duplicate** (the broker redelivers
because the previous consumer lost its connection before the acknowledgement, or the bus retries) is **not executed**: the
previous delivery may have run the method. The caller gets `METHOD_OUTCOME_UNKNOWN` (see the wire contract). The host does not
know which methods are idempotent for this purpose, so every method is treated as non-idempotent (`execution.idempotent` only lets a *keyed call* skip the idempotency store, see [the idempotency filter](method-handler-sdk-idempotency.md#idempotency-filter-at-band-300)). The invocation id check stays as a second
line (a repeated id is answered `METHOD_OUTCOME_UNKNOWN` as well, also after a refused redelivery). The log line of a refusal names
the invocation id, the method, the tenant and the delivery info (redelivered flag, retry attempt, redelivery count, message id)
and never the parameters or the token.

**One helper for every host (OD-3).** The decision "must this delivery not run again" lives in one public class,
`CkMethodSeenInvocations.Decide(request, delivery)` (namespace `Meshmakers.Octo.Services.Methods.Handlers`), returning
`CkMethodDeliveryDecision.Fresh`, `PossibleDuplicate` (the transport flags the delivery) or `AlreadySeen` (the invocation id is
known). A host that consumes `CkMethodRequest` outside this library (for example the communication controller) must use the
helper instead of keeping its own seen-ids dictionary: it marks the id before it evaluates the flag (a refused redelivery is
remembered), clamps the deadline before it adds the retention (`DateTime.MaxValue` from a foreign sender cannot overflow) and
takes a `TimeProvider` for tests. Keep one instance per host (singleton); the memory is per instance. On `PossibleDuplicate` and
`AlreadySeen` the host answers `METHOD_OUTCOME_UNKNOWN` and does not run the handler; the logging and the refusal counter are the
host's. The helper reads only the invocation id and the deadline and never logs.

**No automatic retry.** The platform never runs a method twice on its own: the dispatcher sends once (no publisher-side
resend; a transport fault ends as `METHOD_TIMEOUT` or `METHOD_HANDLER_NOT_AVAILABLE`), the handler endpoint suppresses faults and the
consumer swallows a failing response send, so the bus retry cannot run the method again. A handler that throws after a side
effect ran once; the caller gets `METHOD_HANDLER_FAILED`. Tests: a counting handler on the RabbitMQ fixture, and a real broker
redelivery (the connection of the busy instance is closed with `rabbitmqctl close_connection`, the second instance gets the message
flagged and refuses it).

**Limits: what is NOT detected.** There is no "at most once" guarantee.

* The same logical operation with a **new invocation id** (caller retry after a timeout, double click). Helps: the idempotency key
  (F3.5, filter band 300). A method declared `execution.idempotent` needs no key: it is safe to repeat.
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

## Calling a method: the dispatcher

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
services.AddCkMethodInvocationFilter<MyAuthorizationFilter>();   // filters: see method-handler-sdk-in-process.md, run for both transports

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
2. **Prefer in-process.** A method with a handler in the same service - registered with `AddCkInProcessMethodHandlers`, local
   only (`HandleLocalOnly`), or as a bus handler of the service (`AddCkMethodHandlers`) - runs through
   `ICkInProcessMethodTransport` (pipeline transport name `in-process`); the bus and the registry are not touched. See [REST facades](method-handler-sdk-in-process.md#rest-facades-a-service-calls-a-method-it-hosts-itself).
3. **Otherwise the bus.** `ICkMethodInvocationPipeline.ExecuteAsync("bus", request, send, ct)` where `send` is the dispatcher's
   only own code: one mandatory, non-persistent `GetCommandResponseAsync` to the shared queue `CkMethodQueueNames.For(methodId)`.
   Handlers consume that queue as competing consumers, so replicas of one service share the traffic.

The dispatcher adds **no filter and no pipeline of its own**: audit, authorization and idempotency are `ICkMethodInvocationFilter`s
and run once per call for both transports.

**Outcomes (the caller sees an envelope, never an exception)**

| Situation | Envelope |
|---|---|
| Handler answered | the handler's `CkMethodResponse` unchanged (success, declared error, `METHOD_HANDLER_FAILED`, `METHOD_TIMEOUT`, `METHOD_OUTCOME_UNKNOWN`, ...) |
| The registry knows no live handler (never announced, local only in another service, withdrawn, expired) | `METHOD_HANDLER_NOT_AVAILABLE`, parameters `methodId` and `reason` = `no-live-handler`, no stack trace, **nothing sent**; inside the registry warm-up the call first waits for an announcement |
| A live entry is stale (handler crashed inside its TTL): the broker returns the mandatory send | the same envelope (`no-live-handler`) within milliseconds; the entry is expired, so the next call is refused before the send |
| The registry knows live handlers, none serves the tenant | `METHOD_HANDLER_NOT_AVAILABLE`, `reason` = `tenant-not-served`, nothing sent |
| The request carries a newer contract version than every live handler announced | `METHOD_CONTRACT_VERSION_UNSUPPORTED`, nothing sent (wire contract rule 4) |
| No answer before `DeadlineUtc` + `ResponseGrace` (2 s), or the deadline has passed already | `METHOD_TIMEOUT` (outcome unknown), nothing is re-sent |
| Bus fault, bus stopping, filter or resolver exception | `METHOD_HANDLER_FAILED` with `correlationId` (details only in the log) |
| Caller cancels its token | `OperationCanceledException` (the only exception) |

**Timeouts.** The deadline is `request.DeadlineUtc`, the handler's cancellation token fires at it and the handler answers
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
   omitted); the first dispatcher release used that fallback only, so a method with `timeoutSeconds: 2` reported 15. A request that arrives after its
   deadline is not executed (`METHOD_TIMEOUT`). The host never throws for a deadline: `DateTime.MaxValue` runs the handler (the token
   delay is capped at ~24 days), `DateTime.MinValue` is expired, and the seen-ids retention is computed from `min(DeadlineUtc, now + 1 h)`.
4. **In-process.** The same clamp runs in front of the in-process branch, the handler token fires at the deadline and a handler that
   ignores it is not waited for past `DeadlineUtc` (same envelope as on the bus, tests compare them).

**The registry is the gate of the bus send.** `ICkMethodHandlerRegistry` (fed by `CkMethodHandlerAnnounced` / `Withdrawn`,
expiry = receipt time + the announced TTL, 45 s by default; a dispatcher asks for a fresh announcement at start) decides whether a request
leaves the process, and provides the tenant and contract version checks and diagnostics (`GetLiveHandlers`, `GetLastSeen`, `Snapshot`;
the Studio view is F3.2). Before it became the gate it was advisory and the broker's mandatory flag decided; that meant one send per call of a method
nobody serves, and the request carries the parameters (the new password of `User.ResetPassword-1`, `CreateUser-1`, ...) and the caller's
access token. RabbitMQ returned it unroutable and did not store it, but it had reached the broker, and MassTransit logged every return as
`ERROR S-FAULT ... CkMethodRequest`. The rules now:

* **No live handler, no send.** Without a live entry for the method the dispatcher answers `METHOD_HANDLER_NOT_AVAILABLE` /
  `no-live-handler` (same envelope, same WARN log line, same `ck.method.calls` / `ck.method.no_handler` counting as the broker's return)
  and calls nothing on the event hub. This is generic: every method of every handler host, not only the identity methods. A local-only
  handler (`HandleLocalOnly`, e.g. the identity password methods with `Methods:Identity:EnablePasswordMethods=false`) never announces
  itself, so it is refused before the send in every other service.
* **Warm-up.** A freshly started dispatcher has not heard every handler yet. Until `Methods:Dispatcher:RegistryWarmUp` (default 20 s,
  longer than the 15 s heartbeat; the discovery request at start restarts it) has passed, a call whose method the registry has **not heard
  of** (or knows only from the restored snapshot) waits for an announcement of that method, at most until the warm-up ends or the request
  deadline passes, and is then sent or refused. A withdrawal or an expiry the registry received itself is an answer: such a call is refused
  at once, also inside the warm-up. Zero turns the wait off.
* **A live entry is not a guarantee.** The send stays mandatory. A handler that crashed (no withdrawal) stays live for at most its TTL; the
  first call in that window is sent and returned by the broker (`no-live-handler`), the entry is expired (`NoQueueBound`) and every
  further call is refused before the send. This window (one call per crashed instance, at most the TTL) is the remaining case in which a
  request reaches the broker without a consumer; the S-FAULT line MassTransit writes for it holds the address, message id and type only,
  never the body.
* **Races.** A handler that starts announces itself only after its endpoint accepts messages; a call in the milliseconds before the
  announcement arrives is refused (safe direction, the caller may retry). A graceful stop withdraws before the endpoints go, so new calls
  are refused at once. Several dispatcher replicas each keep their own registry from the same broadcasts (and share the snapshot), so they
  decide alike.
* **Emergency switch.** `Methods:Dispatcher:RefuseWithoutLiveHandler=false` (env `OCTO_Methods__Dispatcher__RefuseWithoutLiveHandler`)
  restores the advisory registry: an empty registry does not refuse and the broker decides. Use it only when the registry feed is broken
  (handlers announce, but the dispatcher does not receive the broadcasts); it lets requests of unroutable calls, with their parameters,
  reach the broker again.

A withdrawal beats any announcement written before it; a broker "not routable" expires only entries last seen before the send.

**The persisted snapshot.** When the host registers `ICkMethodHandlerSnapshotStore` (the MongoDB runtime engine does:
non-CK collection `ck_method_handler_snapshot` in the system database, one document per method and handler instance, unique key plus a
TTL index) the registry is written to it in the background (`Methods:Dispatcher:SnapshotFlushInterval`, default 5 s, one bulk write per
heartbeat round) and loaded by `CkMethodHandlerSnapshotLoadService` (initialization order 28, before the discovery request). A restarted
dispatcher host therefore knows its handlers, the served tenants and the contract versions before the first heartbeat arrives.

* **A row is "last seen", not "live".** A restored row is live only inside its TTL window (`ExpiresUtc`, receipt time + announced TTL),
  exactly like a received announcement. Rows of a stopped handler, an expired TTL or a withdrawal are history: `GetLiveHandlers` does not
  return them. Inside the registry warm-up a call waits for a fresh announcement of the restarted handler; after it the call is refused as
  `METHOD_HANDLER_NOT_AVAILABLE` without a send. A withdrawal is stored as a tombstone with the
  handler's own withdrawal timestamp, so an older announcement that arrives after a restart cannot resurrect the instance.
* **Never more than an announcement could say.** On restore `LastSeenUtc` is capped to now and `ExpiresUtc` to `LastSeenUtc` + 24 h; rows
  last seen more than 24 h ago are skipped. A heartbeat or discovery answer that arrived since is newer and wins over the snapshot row.
* **Service data only.** A row holds ids, service name, versions, times, the served tenant ids and flags; no parameter value, token or user
  data (tests over the stored document and the row type).
* **Optional.** Without the store, or with `Methods:Dispatcher:SnapshotEnabled=false`, nothing is queued and the registry behaves as before.
  A store that is down, or not yet there (system tenant missing), never affects a call: the write is retried with the next flush, a failing
  load is logged and the registry fills from heartbeats.
* Several dispatcher replicas share the collection; an upsert never replaces a row with an older `LastSeenUtc`.

**Registry diagnostics.** `ICkMethodHandlerRegistryDiagnostics.GetDiagnostics()` (public, read-only, registered by
`AddCkMethodDispatcher`) tells a live handler from a restored row and reports the health of the snapshot:

* **Source.** `CkMethodHandlerEntry.Source` is `Announcement` for a row received as a heartbeat, a discovery answer or a withdrawal, and
  `Snapshot` for a row restored from the persisted snapshot and not yet confirmed by its handler. An announcement or withdrawal of the same
  instance replaces it. The source is derived at restore time and not stored.
* **State.** `entry.GetState(nowUtc)` is `Live`, `Expired` (heartbeat missed), `Withdrawn` or `NoQueueBound` (the broker returned a mandatory
  send), by the same rules as `GetLiveHandlers`. A restored row is never `Live` after its `ExpiresUtc`.
* **Snapshot.** `Enabled` (store present and `SnapshotEnabled`), the last load (time, rows read, rows restored, failed flag), the last
  successful flush time and whether the last flush failed. A failure is a flag plus the exception **type name**, never the message (it may
  carry connection data).
* **Also returned.** The dispatcher instance id, every entry with source and state, and the method ids of the in-process transport
  (`ICkInProcessMethodTransport`, transport `in-process`; they never announce); empty without such a transport.
* **Service data only.** Ids, service name, versions, times, served tenants and flags; no parameter value, token or user data (allow-list
  test over the result types). The GraphQL query that exposes it to system administrators lives in octo-asset-repo-services.

**A handler stopping mid-flight.** A graceful stop withdraws first and removes the endpoints; new calls fail at once with
`METHOD_HANDLER_NOT_AVAILABLE`. A call that is running on that instance gets its cancellation token cancelled and ends as
`METHOD_TIMEOUT` (outcome unknown) or, when the handler finished first, with its result. It is never lost silently and never run twice.
The `timeoutSeconds` of that `METHOD_TIMEOUT` is the timeout of the request, not a host default.

**Tests.** `tests/Methods.Tests/Dispatching` (dispatcher with a faked hub, registry races, declaring-element resolution, registration;
Registry gate: no call on the event hub without a live handler, warm-up wait and its ends, withdrawal inside the warm-up, stale entry after a
returned send, emergency switch) and `tests/Methods.IntegrationTests/CkMethodDispatcherBusTests.cs` (private RabbitMQ: the same cases over
the bus and in-process, no consumer, consumer gone, mid-flight stop, timeout, derived type, replicas; registry gate: the send exchange of a
refused method does not exist on the broker, with the emergency switch off it does, a gateway started before its handler waits for the
announcement).

## Replicas and sharing

Several instances of a service (replicaCount > 1) all register the same method endpoint; the broker delivers each call to exactly one of
them. Nothing has to be configured. Proof, a rolling-update caveat (a queue with different arguments blocks the start of the new build,
never the old replicas) and the measured dispatch overhead (p95 about 1 ms on loopback) are in `docs/method-performance.md`.
