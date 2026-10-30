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

## Idempotency store latency (AB#7824, F3.5)

A call with an idempotency key adds two writes to the dispatch path: claim (insert) and completion (update) in the tenant database
(`MethodIdempotency`, asset-repo `MongoMethodIdempotencyStore`). Unkeyed calls are not affected. Measured on a single-node replica
set in a container with majority write concern: claim p50 3.4 ms / p95 6.0 ms, completion p50 3.8 ms / p95 7.0 ms, **claim + complete
p50 7.5 ms / p95 12.0 ms**. Method, repeat command and the full table: `docs/method-idempotency-store.md` in
octo-asset-repo-services. The 20 ms dispatch budget above is for the unkeyed path; test-2 numbers follow at the Phase 3 gate (AB#7829).

## Measurements: redelivery under forced failure and claim write cost (AB#7823, F3.5)

Numbers behind OD-3 option A (handler host refuses a flagged redelivery, caller gets `METHOD_OUTCOME_UNKNOWN`) and the trigger for option
B' (handler-side claim for keyed calls), instead of derived ones. Also the baseline for the idempotency store (AB#7824).

Project `tests/Methods.Measurements` (+ `tests/Methods.Measurements.Worker`, the replica that is hard-killed). It is **not part of the
default run**: the CI test step takes `**/*Tests.csproj` and the project name does not match; every test is additionally skipped unless
`OCTO_METHOD_MEASURE=1`, so a plain `dotnet test` of the solution reports skips and starts no container. Private Testcontainers only
(`rabbitmq:3-management`, `mongo:8.0.15` single-node replica set), removed after the run; no shared stack, no test-2.

### Repeat

```bash
dotnet build Octo.Common.Services.sln -c DebugL            # builds the worker the hard-kill scenario starts as a child process
export OCTO_METHOD_MEASURE=1
export OCTO_METHOD_MEASURE_ROUNDS=10                       # optional, default 10 rounds of 300 calls per scenario
dotnet test tests/Methods.Measurements/Methods.Measurements.csproj -c DebugL --no-build \
  --logger "console;verbosity=detailed"                    # needs Docker; about 5 minutes; lines "RESULT ..." and "MEASURE ..."
# one part only: --filter "FullyQualifiedName~RedeliveryMeasurementTests"  or  "FullyQualifiedName~ClaimWriteBenchmarkTests"
```

### Method

**Forced failure** (`RedeliveryMeasurementTests`). Two handler replicas of one service share the method queue behind one dispatcher
(gateway host). Per round 300 calls with 32 in flight; the handler records its execution on entry (so a kill during its 400 ms of work
still counts) and then works 400 ms. After the 10th completed call one failure is forced:

| Scenario | How the failure is forced |
|----------|---------------------------|
| `ConnectionClose` | `rabbitmqctl close_connection` on the consumer connection that holds the most unacknowledged messages (a lost connection) |
| `HardKill` | `SIGKILL` of the replica, which runs as a child process of the test (`Process.Kill` on the handle the test created): no close, no ack |
| `GracefulStop` | the replica is stopped through the host shutdown (heartbeat down, withdraw, endpoints removed, bus stopped) |

10 rounds per scenario, each with fresh hosts and a fresh instance prefix: **3000 invocations per scenario**, one forced failure per
round. Per invocation id the test counts how often the handler body ran. An id that ran twice is a duplicate the broker did **not**
flag (the flag makes a host refuse the delivery without running the handler). Two runs on different instances are the *unrecognized
cross-instance duplicate*: `ck.method.duplicate_refused` (AB#6920) counts only recognized duplicates (a `seen_id` refusal needs the same
instance) and can never show it, so this measurement is the only evidence for it. The library's own count comes from a `MeterListener` on
`ck.method.duplicate_refused` (reason `broker_flag` / `seen_id`); the broker's own `redeliver` counter (management API) is the independent
witness.

**Claim write** (`ClaimWriteBenchmarkTests`). Document, unique `_id` (SHA-256 hex), TTL index and filters mirror
`MongoMethodIdempotencyStore` (octo-asset-repo-services): claim = `InsertOne` with unique `_id`, completion = `UpdateOne` conditional on
`_id`, `firstInvocationId`, `state = 0` and `expiresAt > now`, with a 700 byte response. Write concern `majority`, raw MongoDB driver
(no engine, no authentication) against the container on loopback, 500 warm-up calls and 5000 measured calls (sequential), then 5000 with 8
and with 32 calls in flight.

### Results (2026-10-30)

Setup: Apple M4 Pro (14 cores, 64 GB), macOS 27.0.1, Docker 29.8.0 (linux/arm64, VM with 14 CPUs), `rabbitmq:3-management` = RabbitMQ
3.13.7, `mongo:8.0.15` (single-node replica set, `w: majority`), MongoDB.Driver 3.11.1, .NET 10.0.13 (SDK 10.0.402), DebugL, commit of
this change on top of `45b82d0`. Gateway, handlers and containers on one machine (loopback). Other containers of other sessions were
running on the same Docker VM, so treat single figures as noisy (the previous measurement of this page had a quiet machine).

**Forced failure, 2 replicas** (3000 invocations and 10 forced failures per scenario):

| Scenario | Invocations | Executions (handler ran) | Flagged redeliveries (refused, not run) | Broker `redeliver` counter | Unflagged duplicates | of which cross-instance, unrecognized | `seen_id` refusals |
|----------|-------------|--------------------------|-----------------------------------------|----------------------------|----------------------|---------------------------------------|--------------------|
| Connection close | 3000 | 3000 | 160 (16 per failure) | 160 | **0** | **0** | 0 |
| Hard kill | 3000 | 3000 | 240 (24 per failure) | 240 | **0** | **0** | 0 |
| Graceful stop | 3000 | 2971 | 29 (2.9 per failure) | 29 | **0** | **0** | 0 |

What the caller saw: connection close 2840 success + 160 `METHOD_OUTCOME_UNKNOWN`; hard kill 2760 + 240; graceful stop 2971 + 29; no
`METHOD_TIMEOUT`, no other error, every call answered. Of the refused deliveries, 160 / 240 had **already run** on the replica that lost
its connection (the caller is told "unknown", the effect happened once: this is the at-most-once price of option A), whereas the 29 of the
graceful stop had **never run** (the stopping replica had received them and handed them back before starting them; the caller is told
"unknown" for a call that did not execute).

Reading:

- Every redelivery the broker produced was flagged, and every flagged delivery was refused: flagged refusals equal the broker's own
  `redeliver` counter in all three scenarios. **No invocation ran twice, on the same or on another instance**: 0 unflagged duplicates in
  9000 invocations and 30 forced failures (429 redeliveries). With zero events observed the 95 % upper bound of the unflagged rate is about
  3 / 9000 = 0.03 % of the invocations in these scenarios. B' is **not** triggered by this measurement.
- The graceful stop is **not** free of redeliveries when calls are in flight: the earlier statement "a graceful stop redelivers nothing"
  (Replica sharing above) holds for a stop in a quiet moment; with 32 calls in flight about 3 deliveries per stop come back flagged and
  were refused without having run. They show as `METHOD_OUTCOME_UNKNOWN` to the caller although the handler never ran, and as
  `ck.method.duplicate_refused{reason=broker_flag}`. Not a correctness problem of option A (nothing ran twice); it is a rate to know for
  rolling updates.
- Not covered, because the broker cannot produce them: a caller that re-sends the same invocation id (the dispatcher never does) and
  a consumer that acknowledges late after the broker's consumer timeout. A duplicate of that kind would run on a second instance without a
  flag; the metric would not show it either.

**Claim write, majority write concern** (single-node replica set, loopback):

| Operation | Calls in flight | p50 | p95 | p99 |
|-----------|-----------------|-----|-----|-----|
| Claim: unique insert | 1 | 0.45 ms | 0.82 ms | 1.57 ms |
| Completion: conditional update | 1 | 0.92 ms | 1.78 ms | 2.16 ms |
| **Claim + complete** | 1 | **1.52 ms** | **2.31 ms** | 3.41 ms |
| Losing claim (duplicate key) | 1 | 0.27 ms | 0.38 ms | 0.51 ms |
| Claim: unique insert | 8 | 1.14 ms | 3.70 ms | 4.92 ms |
| Completion: conditional update | 8 | 1.34 ms | 3.82 ms | 5.14 ms |
| **Claim + complete** | 8 | **2.68 ms** | **5.84 ms** | 6.74 ms |
| Claim: unique insert | 32 | 2.72 ms | 4.86 ms | 16.19 ms |
| Completion: conditional update | 32 | 2.86 ms | 4.95 ms | 10.45 ms |
| **Claim + complete** | 32 | **5.74 ms** | **9.15 ms** | 23.61 ms |

Throughput of claim + complete on one node: about 2600 per second with 8 and 4900 per second with 32 in flight. These are the cost of
the two writes on the driver level; the store in octo-asset-repo-services adds its own overhead (tenant database lookup, admin client,
timeouts), which is why its figures in `docs/method-idempotency-store.md` (claim + complete p50 7.5 ms / p95 12 ms) are higher. A replica
set across nodes adds the majority acknowledgement round trip (network); test-2 numbers follow at the Phase 3 gate (AB#7829).
