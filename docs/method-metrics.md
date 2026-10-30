# CK method metrics and traces (AB#6920, F3.5)

An operator sees the rate, duration and failures of method calls per method, and follows one call from the gateway over
the event hub into the handler in a single trace. Everything is on the meter and the span source named
`Meshmakers.Octo.Methods`; both are subscribed by `AddObservability()` (`ObservabilityBuilder`), together with the
`MassTransit` span source that carries the trace over the bus.

## Meter `Meshmakers.Octo.Methods`

| Instrument | Type | Labels | What it counts |
|------------|------|--------|----------------|
| `ck.method.calls` | counter | `method_id`, `transport`, `outcome` | every finished call, once |
| `ck.method.duration` | histogram, seconds (buckets 5 ms to 300 s) | `method_id`, `transport`, `outcome` | duration of the same call as the caller sees it |
| `ck.method.no_handler` | counter | `method_id`, `transport` | calls nobody could take: outcome `METHOD_HANDLER_NOT_AVAILABLE` (no live handler, tenant not served, method not registered). A refusal because the idempotency store was down carries the same code but is not counted here. |
| `ck.method.duplicate_refused` | counter | `reason`, `method_id` | deliveries a handler host refused as a possible duplicate and did **not** execute |
| `ck.method.audit.failures` | counter | `transport` | audit events that could not be written (AB#6919) |

In Prometheus/Dash0 the names appear with underscores (`ck_method_calls_total`, `ck_method_duration_seconds_bucket`,
`ck_method_no_handler_total`, `ck_method_duplicate_refused_total`).

Label values:

| Label | Values |
|-------|--------|
| `method_id` | qualified method id without model version, e.g. `System.Identity/User.ChangePassword-1`; at most 1000 distinct values per process, further ones are `other` |
| `transport` | `bus` (remote handler over the event hub), `in-process` (handler in the calling service), `gateway` (refused by the gateway before the pipeline; only calls and duration), `other` |
| `outcome` | `SUCCESS`, `ACCEPTED` (long-running operation), `CALLER_CANCELLED`, a platform error code (`METHOD_FORBIDDEN`, `METHOD_TIMEOUT`, `METHOD_HANDLER_FAILED`, `METHOD_HANDLER_NOT_AVAILABLE`, `METHOD_OUTCOME_UNKNOWN`, ...) or an error code the method declares; `invalid_code` for anything else. Same vocabulary as the audit event. |
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
