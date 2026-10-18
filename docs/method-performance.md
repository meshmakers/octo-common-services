# CK method dispatch: replica sharing and dispatch overhead (AB#6908, F3.3)

What is proven, how to repeat it, and the numbers. All tests run against a **private RabbitMQ container** (Testcontainers,
`rabbitmq:3-management`, started per test run) and several handler hosts inside one test process; every host has its own
service provider, event hub bus and handler instance id (`CkMethodResponse.HandlerInstanceId`).

## Replica sharing

All replicas of a service register the same command endpoint `CkMethodQueueNames.For(methodId)`: one durable, auto-delete,
**non-exclusive** queue; RabbitMQ delivers each message to exactly one of the competing consumers. The dispatcher sends to the
stable exchange `{prefix}-{queue}::send`, so it neither knows nor cares how many replicas exist.

Tests (`tests/Methods.IntegrationTests/CkMethodReplicaSharingBusTests.cs`):

| Test | Proves |
|------|--------|
| `Replicas_ShareOneQueue_...` (2 replicas / 1000 calls, 3 replicas / 3000 calls) | every caller gets one envelope; every invocation id executed exactly once; the envelope's `HandlerInstanceId` adds up to the replicas' own counters; each replica handled at least 30 % (2 replicas) / 20 % (3 replicas) of the calls |
| `ReplicaStoppedMidBurst_...` | one of three replicas is stopped gracefully while 1500 calls are in flight: no call lost, none executed twice, every failure (if any) is `METHOD_TIMEOUT` or `METHOD_OUTCOME_UNKNOWN`, the stopped replica takes no further call, a follow-up burst succeeds on the remaining two |
| `LastReplicaStopped_...` | no replica left: new calls fail fast with `METHOD_HANDLER_NOT_AVAILABLE`; a new replica is served again |

Observed (Apple M4 Pro): 2 replicas `[497, 503]` of 1000, 3 replicas `[1000, 1000, 1000]` of 3000 (RabbitMQ round-robin with a 2 ms
handler). With the replica stopped at call 300 of 1500 the burst ended without a single failure: a graceful stop lets the
consumer finish what it received, the broker redelivers nothing. `METHOD_TIMEOUT` / `METHOD_OUTCOME_UNKNOWN` appear only for a
hard loss of the instance (see `CkMethodDispatcherBusTests.BrokerRedelivery_...`).

### A queue that already exists with different arguments (rolling update)

If an older build declared the method queue with other arguments than the new build, the broker answers `PRECONDITION_FAILED`
and the new replica cannot attach. Behaviour (`CkMethodQueueArgumentsBusTests`):

- The new replica does **not** take the queue over, delete it or announce itself. Its start fails after `Methods:EndpointReadyTimeout`
  (30 s) with a `TimeoutException` that names the queue and the likely cause (the message did not name it before AB#6908).
- The old queue and the old replicas are untouched and keep serving; callers see no failure. In Kubernetes the new pod does not become
  ready, so the rollout stalls on the old pods instead of causing an outage (restart loop of the new pod until the rollout is fixed).
- As soon as the old queue is gone (last old replica stopped, auto-delete) the new build starts and serves.
- **Rule for changes:** never change the arguments of an existing shared method queue in place. A change of the queue arguments needs a
  new queue name (new topology version in `CkMethodQueueNames`) so that both builds can run side by side.

## Dispatch overhead

`DispatchOverhead_WithANoOpHandler_StaysWithinTheBudget` (category `Performance`): a no-op handler, so the dispatcher round trip
(resolve declaring element, pipeline, mandatory publish, queue, handler host, response) **is** the overhead. 300 warm-up calls, then
3000 sequential calls, once with 1 and once with 3 replicas; p50/p95/p99 go to the test output.

- Default (CI, shared agents): asserts p95 below **250 ms**, a deliberately generous bound against noise.
- `OCTO_METHOD_PERF_STRICT=1`: asserts the story's target, p95 below **20 ms**.

### Results (2026-10-18)

Setup: Apple M4 Pro (14 cores, 64 GB), macOS, Docker 29.8 (linux/arm64) with `rabbitmq:3-management` (single node, default
settings, publisher confirms on), .NET 10.0.12, DebugL. Gateway, handlers and broker on the same machine (loopback), so the
network share is minimal. Three runs of each case:

| Replicas | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
|----------|----------|----------|----------|----------|
| 1 | 0.53 - 0.54 | 0.70 - 0.79 | 1.12 - 1.20 | 2.7 - 5.6 |
| 3 | 0.66 - 0.70 | 0.83 - 1.11 | 1.46 - 1.83 | 2.6 - 18.7 |

p95 is about 1 ms, i.e. about 20 times below the 20 ms target. Latency is the same with one or three replicas: sharing costs nothing
measurable. The figures are a local-loopback lower bound; a cluster adds network round trips to the broker (two hops) and the
response exchange, and handler time on top.

### Repeat

```bash
export OCTO_METHOD_PERF_STRICT=1   # optional: assert the 20 ms target
dotnet test tests/Methods.IntegrationTests/Methods.IntegrationTests.csproj -c DebugL \
  --filter "Category=Performance" --logger "console;verbosity=detailed"
# replica tests
dotnet test tests/Methods.IntegrationTests/Methods.IntegrationTests.csproj -c DebugL \
  --filter "FullyQualifiedName~ReplicaSharing|FullyQualifiedName~QueueArguments" --logger "console;verbosity=detailed"
```

Needs Docker. Close other heavy processes for the strict run. Not covered: the identity service as a real replica set (no method
handler there yet, F4.2, AB#5672; re-run when the first identity handler exists), long-running soak, and cross-node broker clusters.
