---
description: CK method metrics and traces: instruments, labels, where a call is counted, one trace per call.
applies_to: src/Methods/Metrics/**, src/Observability/ObservabilityBuilder.cs
---

# CK method metrics and traces

An operator sees the rate, duration and failures of method calls per method, and follows one call from the gateway over
the event hub into the handler in a single trace. Everything is on the meter and the span source named
`Meshmakers.Octo.Methods`; both are subscribed by `AddObservability()` (`ObservabilityBuilder`), together with the
`MassTransit` span source that carries the trace over the bus.

## Meter `Meshmakers.Octo.Methods`

| Instrument | Type | Labels | What it counts |
|------------|------|--------|----------------|
| `ck.method.calls` | counter | `method_id`, `transport`, `outcome` | every finished call, once |
| `ck.method.duration` | histogram, seconds (buckets 5 ms to 300 s) | `method_id`, `transport`, `outcome` | duration of the same call as the caller sees it |
| `ck.method.no_handler` | counter | `method_id`, `transport` | calls nobody could take: outcome `METHOD_HANDLER_NOT_AVAILABLE` (no live handler, tenant not served, method not registered). A refusal because the idempotency store was down carries the same code but is not counted here. A call without a live handler is refused by the dispatcher **before** the send (nothing reaches the broker); it is counted exactly as the broker's return was before (`transport` `bus`, `reason` `no-live-handler`). |
| `ck.method.duplicate_refused` | counter | `reason`, `method_id` | deliveries a handler host refused as a possible duplicate and did **not** execute |
| `ck.method.audit.failures` | counter | `transport` | audit events that could not be written |
| `ck.method.operations` | counter | `method_id`, `state` | state changes of operations of asynchronous methods: `accepted`, `running`, `succeeded`, `failed`; operations the sweep ends as lost (`METHOD_OUTCOME_UNKNOWN`, `handler-lost`) count as `failed` with `method_id` `other` |

In Prometheus/Dash0 the names appear with underscores (`ck_method_calls_total`, `ck_method_duration_seconds_bucket`,
`ck_method_no_handler_total`, `ck_method_duplicate_refused_total`).

Label values:

| Label | Values |
|-------|--------|
| `method_id` | qualified method id without model version, e.g. `System.Identity/User.ChangePassword-1`; at most 1000 distinct values per process, further ones are `other` |
| `transport` | `bus` (remote handler over the event hub), `in-process` (handler in the calling service, also a bus handler or a local-only handler called by its own host, e.g. a REST facade), `gateway` (refused by the gateway before the pipeline; only calls and duration), `other` |
| `outcome` | `SUCCESS`, `ACCEPTED` (long-running operation), `CALLER_CANCELLED`, a platform error code (`METHOD_FORBIDDEN`, `METHOD_TIMEOUT`, `METHOD_HANDLER_FAILED`, `METHOD_HANDLER_NOT_AVAILABLE`, `METHOD_OUTCOME_UNKNOWN`, ...) or an error code the method declares; `invalid_code` for anything else. Same vocabulary as the audit event. A handler or filter whose write the database did not acknowledge in time (write-concern error, unknown commit result) counts as `METHOD_OUTCOME_UNKNOWN`, not `METHOD_HANDLER_FAILED`. |
| `state` | `accepted`, `running`, `succeeded`, `failed` (`ck.method.operations` only) |
| `reason` | `broker_flag` (the broker flagged the delivery as a possible duplicate), `seen_id` (no broker flag, but this instance has seen the invocation id) |

**Never in a label:** parameter values, results, tokens, idempotency keys, entity ids (`targetRtId`), user names or subjects,
and the tenant id. The Methods meter does not carry the tenant, like the audit counter before it.

### How a call is counted

* Calls through the dispatcher or the in-process transport run through one invocation pipeline; it is counted around the
  whole pipeline (authorization refusals, replays from the idempotency store, exceptions and cancellations included), once.
  A keyed call to a method declared `execution.idempotent: true` skips the idempotency store and is counted once like an unkeyed call.
  It is not a filter and has no place in the order bands (Audit 100, Authorization 200, Idempotency 300).
* A call that the **gateway** refuses before it reaches the pipeline (forbidden, unknown target, parameters too large, ...)
  is counted by the audit recorder that records the refusal, with `transport="gateway"`. It is therefore visible wherever
  the method audit is registered.
* `ck.method.duplicate_refused` is counted by the shared duplicate-delivery helper (`CkMethodSeenInvocations`) of every
  handler host. A rising `seen_id` while `broker_flag` stays flat means duplicates the broker did not flag: the trigger
  for OD-3 option B' (see `methodConsistency.md`).

### Example queries (PromQL)

```promql
# calls per second per method and outcome
sum by (method_id, outcome) (rate(ck_method_calls_total[5m]))
# failure ratio per method (everything but SUCCESS/ACCEPTED)
sum by (method_id) (rate(ck_method_calls_total{outcome!~"SUCCESS|ACCEPTED"}[5m]))
  / sum by (method_id) (rate(ck_method_calls_total[5m]))
# p95 duration
histogram_quantile(0.95, sum by (le, method_id) (rate(ck_method_duration_seconds_bucket[5m])))
# unroutable calls
sum by (method_id) (increase(ck_method_no_handler_total[15m]))
# refused duplicates by reason
sum by (reason) (increase(ck_method_duplicate_refused_total[1h]))
```

## One trace per call

Spans (source `Meshmakers.Octo.Methods`, tags `ck.method.id`, `ck.method.transport`; never parameters or tokens):

| Span | Where | Notes |
|------|-------|-------|
| `CK method call` (Client) | the pipeline of the calling service | one per call; tag `ck.method.outcome` is set when the call ends; child of the span of the gateway request |
| `CK method handle` (Consumer / Internal) | the bus consumer of the handler host, or the in-process handler | child of the MassTransit process span (bus) or of the call span (in-process) |

Over the bus the chain is: gateway request span, `CK method call`, MassTransit send, MassTransit process, `CK method handle`;
all with the same trace id. MassTransit writes the trace context into the message when the `MassTransit` activity source
has a listener, which `AddObservability()` ensures. In a cluster the span sources must also be listed in
`OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES` (charts) so that the injected exporter picks them up; add `MassTransit` and
`Meshmakers.Octo.Methods` there.

Verified by `CkMethodTraceAndMetricsBusTests` (private RabbitMQ, in-memory exporter; the test fails without the source) and
`CkMethodMetricsTests` (meter listener per outcome and transport, refused duplicates by reason, canary on every label).

### Method metrics and one trace per call

Meter **`Meshmakers.Octo.Methods`** (`CkMethodTelemetry.MeterName`; one `Meter` instance, `CkMethodTelemetry.Meter`, also used by `ck.method.audit.failures`
of the method audit; registered in `ObservabilityBuilder`, test `MeterRegistrationTests.MethodsMeter_IsRegistered`). Code: `src/Methods/Metrics/CkMethodMetrics.cs`,
tests `tests/Methods.Tests/Metrics/CkMethodMetricsTests.cs`, `tests/Methods.IntegrationTests/CkMethodTraceAndMetricsBusTests.cs`, canary in
`CkMethodRedactionBusTests` (metric tags are scanned like logs). Reference for operators: `docs/method-metrics.md`.

| Instrument | Type | Labels | Counts |
|------------|------|--------|--------|
| `ck.method.calls` | counter | `method_id`, `transport`, `outcome` | every finished call once |
| `ck.method.duration` | histogram, seconds (buckets 5 ms .. 300 s) | `method_id`, `transport`, `outcome` | duration of the same call |
| `ck.method.no_handler` | counter | `method_id`, `transport` | outcome `METHOD_HANDLER_NOT_AVAILABLE` of an unroutable call (not the idempotency store being down) |
| `ck.method.duplicate_refused` | counter | `reason` (`broker_flag`, `seen_id`), `method_id` | a delivery a handler host refused as a possible duplicate |
| `ck.method.audit.failures` | counter | `transport` | audit events that could not be written |
| `ck.method.operations` | counter | `method_id`, `state` | state changes of async method operations |

- **Where it is counted (exactly once per call).** `CkMethodInvocationPipeline.ExecuteAsync` wraps *everything* (filters and transport; also the
  no-filter early return) in one call span, one stopwatch and one `finally`: both transports (`bus`, `in-process`) and every outcome (success, declared
  error, platform code, authorization refusal, replay, exception as `METHOD_HANDLER_FAILED`, caller cancellation as `CALLER_CANCELLED`) are seen
  once. It is **not** a filter: the bands Audit 100 / Authorization 200 / Idempotency 300 stay the only ordering contract. A call the **gateway refuses
  before the pipeline** never reaches it, so `CkMethodAuditRecorder.RecordAsync` counts exactly those records (`Transport == "gateway"`, outcome =
  the refusal code, duration = `record.Duration`); records of the audit filter carry `bus`/`in-process` and are not counted there. Consequence: the gateway
  count exists where audit is registered (the recorder is the only shared point); no asset-repo change is needed for it.
- **Duplicates.** `CkMethodSeenInvocations.Decide` counts, so every host (library bus consumer, communication controller) is covered
  once per delivery: the broker flag wins (`broker_flag`), otherwise a repeated invocation id (`seen_id`). A fresh delivery counts nothing. This is the
  measurable trigger of OD-3 option B' (a duplicate without broker flag = `seen_id` rising).
- **Labels never contain** parameters, results, tokens, idempotency keys, entity ids, user names or the tenant (the Methods meter carries no tenant, like
  `ck.method.audit.failures`; tenant is not needed to see which method fails). Bounded values: unknown transport -> `other`; an outcome that is neither
  `SUCCESS`/`ACCEPTED`/`CALLER_CANCELLED` nor a platform code nor a well-formed declared code -> `invalid_code`; after 1000 distinct `method_id`
  values per process further ids -> `other` (`CkMethodLabelBudget`; a forging caller cannot grow the series). Canary test: all labels, instrument names.
- **Traces.** `ObservabilityBuilder` adds `AddSource("MassTransit")`: MassTransit then creates send/process spans and carries the W3C context in the message,
  so the handler joins the gateway's trace. Spans of `Meshmakers.Octo.Methods` (`CkMethodTelemetry`): `CK method call` (Client; the dispatcher side, the
  pipeline's one span, tags `ck.method.id`, `ck.method.transport`, `ck.method.outcome`) and `CK method handle` (Consumer on the bus consumer, Internal for
  in-process); no parameters, results or tokens in tags. Chain over the bus: gateway span > `CK method call` > MassTransit send > MassTransit process > `CK method handle`.
  Test `CkMethodTraceAndMetricsBusTests.CallOverTheBus_IsOneTrace_...` (private RabbitMQ, the tracer provider is the one `AddObservability()` builds, in-memory
  exporter; fails when the `MassTransit` source is removed). Like the other sources it must also be in `OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES` in the charts to leave a pod through the injector.
