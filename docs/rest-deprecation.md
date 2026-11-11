# Deprecating a REST operation (AB#8421, waves AB#8415)

When an operation moves from REST to a CK method (CK v2 Phase 5, Feature F5.7 AB#5705), its REST endpoint stays for
a compatibility window. During that window every caller must see that the endpoint is deprecated and when it goes away,
and the platform must know who still calls it. One attribute does all of that.

The window (decided by Gerald 2026-11-11, replaces the rules of 2026-11-07 and 2026-11-10): an endpoint is
**deprecated from the delivery of the release that contains the replacing method**. There is **no fixed compatibility
window**: the **sunset date is to be announced** — Gerald decides it once the migration functions are implemented. Until
then no sunset date is configured and no `Sunset` header is sent. Neither date is known when the code is merged, so
migration waves take their dates from configuration (`Wave`, below), not from code.

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
- The dates in the attribute are fixed in code. For a migration wave whose dates are only known at rollout, use `Wave`
  instead (next section).
- Nothing else is needed in the service. `AddObservability()` (package `Meshmakers.Octo.Services.Observability`)
  installs the headers and the counter, and `AddOctoApiVersioningAndDocumentation()` (package
  `Meshmakers.Octo.Services.Swagger`) marks the OpenAPI operation. A host that uses neither can call
  `services.AddOctoRestDeprecation()` itself (package `Meshmakers.Octo.Services.RestDeprecation`).
- Endpoints without the attribute are unchanged: no header, no measurement, the same OpenAPI operation.

## Dates from configuration: waves (AB#8415)

```csharp
[HttpPut("{userName}/roles")]
[OctoRestDeprecated(Wave = "IdentityUsers1", Link = "https://docs.meshmakers.cloud/…")]
public async Task<IActionResult> UpdateUserRoles(…) { … }
```

With `Wave`, `Since` and `Sunset` stay empty in the attribute and come from the configuration section
`RestDeprecation:Waves:<wave>` (`RestDeprecationOptions`, bound by `AddOctoRestDeprecation()`):

| Key | Header | Not set |
|-----|--------|---------|
| `RestDeprecation:Waves:<wave>:Since` (`yyyy-MM-dd`) | `Deprecation: @<unix seconds>` | no `Deprecation` header |
| `RestDeprecation:Waves:<wave>:Sunset` (`yyyy-MM-dd`, after `Since`) | `Sunset: <IMF-fixdate>` | no `Sunset` header |

- `Link` (and `Successor`), OpenAPI `deprecated: true` and the counter apply from the first deployment on, with or
  without dates. Without a sunset date the OpenAPI notice is `Deprecated, use <Link>.`.
- In a cluster the keys are environment variables, e.g. `OCTO_RestDeprecation__Waves__IdentityUsers1__Since=2026-12-01`
  (services read `OCTO_`-prefixed variables; keys are case-insensitive). The octo-mesh Helm chart (octo-helm-core) renders
  them from `restDeprecation.waves.<wave>.since|sunset` for every service; a service ignores waves none of its endpoints
  name.
- The values are read once at startup; a change takes effect with the restart that a Helm value change triggers.
- Wave names are letters and digits and start with a letter (they are part of an environment variable name).
- Rollout of a wave (Gerald's rule above): when the release with the wave is delivered, set `since` to the delivery day
  in the values of every cluster and leave `sunset` empty. Once Gerald has decided the sunset date (after the migration
  functions are implemented), set `sunset` on every cluster and enter it on the deprecated-endpoints page
  (octo-documentation, `developerGuide/solutionArchitecture/deprecatedRestEndpoints.md`); until then that page says
  "to be announced".

Current waves:

| Wave | Service | Endpoints | Work item |
|------|---------|-----------|-----------|
| `IdentityUsers1` | octo-identity-services | `PUT users/{userName}/roles`, `PUT` and `DELETE users/{userName}/roles/{roleName}`, `POST users/{userName}/merge` (since 2026-11-08, sunset to be announced) | AB#8415 |

### Startup validation

Without `Wave`, `Since`, `Sunset` and `Link` are required (`Successor` is optional); `Since` and `Sunset` are
`yyyy-MM-dd` and `Sunset` must be after `Since`. With `Wave`, `Since` and `Sunset` must be empty in the attribute, the
wave name must be letters and digits starting with a letter, and the configured dates of the wave follow the same format
and order when both are set (a missing or empty value is fine). In both cases `Link` (and `Successor`, when set) must be an
absolute `http`/`https` URL of printable ASCII characters without `<`, `>` and `"` (percent-encode anything else; Kestrel
writes header values as ASCII). An invalid
attribute or wave configuration fails the service at startup, before it accepts requests:

```text
System.InvalidOperationException: Invalid [OctoRestDeprecated] attribute:
  Meshmakers.Octo.Backend.IdentityServices.….UsersController.ResetPassword (…): Sunset 2027-01-01 must be after Since 2027-01-01
```

## Response headers

Every response of a marked endpoint carries the headers, whatever the status code — 2xx, 400 from model validation,
401/403 from the authorization middleware and 500 from the exception handler included:

| Header | Format | Example |
|--------|--------|---------|
| `Deprecation` | RFC 9745 structured date: `@` + Unix seconds of `Since` (UTC midnight); for a wave only when `Since` is configured | `Deprecation: @1796083200` |
| `Sunset` | RFC 8594, IMF-fixdate of `Sunset`; for a wave only when `Sunset` is configured | `Sunset: Mon, 01 Mar 2027 00:00:00 GMT` |
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
appends `Sunset <yyyy-MM-dd>, use <Link>.` (`Deprecated, use <Link>.` for a wave without a configured sunset) — followed by ` Successor version: <Successor>.` when `Successor` is set —
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
  authenticated principal or without the claim. Pipeline service accounts get the client id
  `octo-pipeline-sa-{adapterRtId}` (adapter-bound) or `octo-pipeline-sa-{configurationRtId}` (standalone)
  (`PipelineServiceAccountProvisioningService.BuildClientId` in octo-communication-controller-services); the runtime id is
  entity data, so every client id with that prefix is reported as the single value `octo-pipeline-sa` (prefix match,
  case-insensitive, before the cap below). A standalone account may also carry a custom client id outside the prefix; that
  one is reported as itself, so a query for `octo-pipeline-sa` does not include it. After 1000 distinct client ids per process, and for ids longer than 200 characters, the value
  is `other`.
- `status_class`: `1xx` … `5xx`.
- Never a tenant id, user name, `sub`, parameter or token — the same rule as `Meshmakers.Octo.Methods`. A test
  (`RestDeprecationTests.Counter_NeverCarriesTenantUserOrSubject`) checks every label value with a `MeterListener`.
  Client ids that carry entity ids are not reported as such: the pipeline service accounts collapse to
  `octo-pipeline-sa` (`RestDeprecationTests.Counter_PipelineServiceAccount_IsReportedWithoutAdapterId`).

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
- `WaveTests` — wave dates from configuration (both, `Since` only, none: Link and counter only), case-insensitive keys,
  empty value as "not set", OpenAPI notice with and without sunset.
- `WaveStartupValidationTests` — `Since`/`Sunset` together with `Wave`, an invalid wave name, a missing link, and a
  malformed or out-of-order configured date fail the start, naming the action and the configuration key; a wave
  without configuration starts.
- `LabelTests` — status classes, `ANY` for endpoints without a method restriction, client id rules (incl.
  `octo-pipeline-sa`).

`tests/Observability.Tests/MeterRegistrationTests.RestDeprecationMeter_IsRegistered_AndStartupFilterInstalled` pins the
meter registration and the startup filter in `AddObservability()`.
