# Deprecating a REST operation (AB#8421)

When an operation moves from REST to a CK method (CK v2 Phase 5, Feature F5.7 AB#5705), its REST endpoint stays for
the compatibility window — one release train and at least three months after the method is GA (decision 13). During
that window every caller must see that the endpoint is deprecated and when it goes away, and the platform must know who
still calls it. One attribute does all of that.

## Marking an endpoint

```csharp
using Meshmakers.Octo.Services.RestDeprecation;

[HttpPost("{userName}/reset-password")]
[Authorize(IdentityServiceConstants.IdentityTenantAdministrationPolicy)]
[OctoRestDeprecated(
    Since = "2026-12-01",                     // GA of the replacing method (UTC day)
    Sunset = "2027-03-01",                    // earliest removal (UTC day), after Since
    Link = "https://docs.meshmakers.cloud/…", // documentation of the replacing method, absolute http(s)
    Successor = "https://…")]                 // optional: the successor operation, absolute http(s)
public async Task<IActionResult> ResetPassword(string userName) { … }
```

- Put it on an action, or on a controller to mark every action of it; an attribute on the action wins over the one on
  the controller.
- Minimal APIs: `.WithMetadata(new OctoRestDeprecatedAttribute { Since = …, Sunset = …, Link = … })`.
- The dates are set in code when the wave goes GA. There is no configuration option.
- Nothing else is needed in the service. `AddObservability()` (package `Meshmakers.Octo.Services.Observability`)
  installs the headers and the counter, and `AddOctoApiVersioningAndDocumentation()` (package
  `Meshmakers.Octo.Services.Swagger`) marks the OpenAPI operation. A host that uses neither can call
  `services.AddOctoRestDeprecation()` itself (package `Meshmakers.Octo.Services.RestDeprecation`).
- Endpoints without the attribute are unchanged: no header, no measurement, the same OpenAPI operation.

### Startup validation

`Since`, `Sunset` and `Link` are required; `Successor` is optional. `Since` and `Sunset` are `yyyy-MM-dd`, `Sunset` must
be after `Since`, and `Link` (and `Successor`, when set) must be an absolute `http`/`https` URL of printable ASCII
characters without `<`, `>` and `"` (percent-encode anything else; Kestrel writes header values as ASCII). An invalid attribute fails the service at
startup, before it accepts requests:

```text
System.InvalidOperationException: Invalid [OctoRestDeprecated] attribute:
  Meshmakers.Octo.Backend.IdentityServices.….UsersController.ResetPassword (…): Sunset 2027-01-01 must be after Since 2027-01-01
```

## Response headers

Every response of a marked endpoint carries the headers, whatever the status code — 2xx, 400 from model validation,
401/403 from the authorization middleware and 500 from the exception handler included:

| Header | Format | Example |
|--------|--------|---------|
| `Deprecation` | RFC 9745 structured date: `@` + Unix seconds of `Since` (UTC midnight) | `Deprecation: @1796083200` |
| `Sunset` | RFC 8594, IMF-fixdate of `Sunset` | `Sunset: Mon, 01 Mar 2027 00:00:00 GMT` |
| `Link` | RFC 9745 relation type `deprecation` | `Link: <https://docs.meshmakers.cloud/…>; rel="deprecation"; type="text/html"` |
| `Link` (only with `Successor`) | RFC 5829 relation type `successor-version`, a further `Link` value | `Link: <https://…>; rel="successor-version"` |

`Deprecation: true` was the expired draft; RFC 9745 uses the structured date. An existing `Link` header (for example
paging relations) is kept; the deprecation link is added as a further value.

How it works: `RestDeprecationStartupFilter` puts `RestDeprecationMiddleware` first in the pipeline. The middleware
registers a `Response.OnStarting` callback; when the response starts, the callback reads the selected endpoint
(or, after an exception, `IExceptionHandlerFeature.Endpoint`, because the exception handler clears the endpoint) and,
when it carries the attribute, writes the headers and counts the call. That is why the headers also appear on
responses MVC never sees.

## OpenAPI

`RestDeprecationOperationTransformer` (`src/Swagger/Transformers`) sets `deprecated: true` on the marked operations and
appends `Sunset <yyyy-MM-dd>, use <Link>.` — followed by ` Successor version: <Successor>.` when `Successor` is set —
to their description (after an existing description, separated by a blank
line). Swagger UI shows the operation struck through. Unmarked operations are not touched.

## Metric

| Meter | Instrument | Type | Labels |
|-------|-----------|------|--------|
| `Meshmakers.Octo.RestDeprecation` | `octo.rest.deprecated.calls` | counter | `endpoint`, `client_id`, `status_class` |

- `endpoint`: HTTP method plus the route **template**, e.g. `POST /{tenantId}/v1/users/{userName}/reset-password`.
  Never the concrete path, which carries tenant ids and user names. An endpoint without an HTTP method restriction is
  reported as `ANY <template>`, so a caller cannot invent label values with made-up methods.
- `client_id`: the `client_id` claim of the authenticated caller (validated token); `anonymous` without an
  authenticated principal or without the claim. The pipeline service accounts `octo-pipeline-sa-<tenant>` carry the
  tenant in their id; all of them are reported as the single value `octo-pipeline-sa` (prefix match, case-insensitive,
  before the cap below). After 1000 distinct client ids per process, and for ids longer than 200 characters, the value
  is `other`.
- `status_class`: `1xx` … `5xx`.
- Never a tenant id, user name, `sub`, parameter or token — the same rule as `Meshmakers.Octo.Methods`. A test
  (`RestDeprecationTests.Counter_NeverCarriesTenantUserOrSubject`) checks every label value with a `MeterListener`.
  Per-tenant client ids are not reported as such: the pipeline service accounts collapse to `octo-pipeline-sa`
  (`RestDeprecationTests.Counter_PipelineServiceAccount_IsReportedWithoutTenant`).

The meter is registered in `ObservabilityBuilder`, so every service that calls `AddObservability()` exports it through
OTLP to Dash0 (and on the internal Prometheus port, if configured).

### Dash0: remaining calls per endpoint and client

In Prometheus/Dash0 the counter appears as `octo_rest_deprecated_calls_total` (same naming as `ck_method_calls_total`,
see `method-metrics.md`). Calls of the last 7 days per service, endpoint and client (PromQL in Dash0):

```promql
sum by (service_name, endpoint, client_id) (
  increase(octo_rest_deprecated_calls_total[7d])
)
```

Only the callers that still need a nudge (successful calls only):

```promql
sum by (service_name, endpoint, client_id) (
  increase(octo_rest_deprecated_calls_total{status_class="2xx"}[7d])
) > 0
```

An endpoint can be removed (F6.2) when the query shows no first-party client and only callers that were told.

## Tests

`tests/RestDeprecation.Tests`:

- `RestDeprecationTests` — TestServer with authentication, authorization and an exception handler: headers on 200, 400,
  401, 403 and 500, the `successor-version` link, controller attribute and action override, unmarked endpoints without header and measurement, label
  values (route template, client id, `anonymous`, status class) and the no-tenant/user/`sub` check.
- `StartupValidationTests` — sunset not after since, relative link or successor, non-ASCII link and a malformed date
  fail the start, naming the action.
- `OpenApiDeprecationTests` — marked operations `deprecated: true` with the notice (and the successor), existing
  description kept, snapshot
  of an unmarked operation.
- `LabelTests` — status classes, `ANY` for endpoints without a method restriction, client id rules (incl.
  `octo-pipeline-sa`).

`tests/Observability.Tests/MeterRegistrationTests.RestDeprecationMeter_IsRegistered_AndStartupFilterInstalled` pins the
meter registration and the startup filter in `AddObservability()`.
