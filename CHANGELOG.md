# Changelog

All notable changes to the `Meshmakers.Octo.Services.*` packages. Versions come from the pipeline (AB#6291), so
entries are grouped by work item until a release assigns the version.

## Unreleased

### Added

- **AB#8415** — `[OctoRestDeprecated(Wave = "<name>", Link = …)]`: the dates of a deprecation wave come from the
  configuration section `RestDeprecation:Waves:<name>` (`Since`, `Sunset`, both optional; in the cluster
  `OCTO_RestDeprecation__Waves__<name>__Since|Sunset` from Helm values) instead of the code, because they are only known
  at rollout. A date that is not configured leaves its header out (`Deprecation` without `Since`, `Sunset` without
  `Sunset`); `Link`, OpenAPI `deprecated: true` (notice `Deprecated, use <link>.` without a sunset date) and the counter
  apply from the first deployment on. An invalid wave name, `Since`/`Sunset` on a wave attribute, or a malformed or
  out-of-order configured date fails the service at startup. Attributes without `Wave` behave as before.

- **AB#8421** — new package `Meshmakers.Octo.Services.RestDeprecation`: `[OctoRestDeprecated(Since, Sunset, Link)]`
  marks a REST action (or controller) deprecated. Every response of a marked endpoint, 4xx/5xx included, carries
  `Deprecation: @<unix seconds>` (RFC 9745), `Sunset: <IMF-fixdate>` (RFC 8594) and
  `Link: <url>; rel="deprecation"; type="text/html"` (plus `Link: <url>; rel="successor-version"`, RFC 5829, with the
  optional `Successor`); the OpenAPI operation is `deprecated: true` with the sunset date
  and link in its description; the counter `octo.rest.deprecated.calls` (meter `Meshmakers.Octo.RestDeprecation`,
  labels `endpoint`, `client_id`, `status_class`; pipeline service accounts `octo-pipeline-sa-*` reported as
  `octo-pipeline-sa`, never with the entity id) counts the remaining calls. Invalid attribute values fail the service
  at startup, naming the action. `AddObservability()` registers the mechanism and exports the meter;
  `AddOctoApiVersioningAndDocumentation()` adds the OpenAPI transformer. Unmarked endpoints are unchanged. See
  `docs/rest-deprecation.md`.

### Fixed

- **AB#8415** — `docs/rest-deprecation.md`, the XML docs and the tests describe the pipeline service account client
  ids correctly: `octo-pipeline-sa-{adapterRtId}` for an adapter's account, `octo-pipeline-sa-{configurationRtId}` for a
  standalone account (or a custom client id outside the prefix, reported as itself), not `octo-pipeline-sa-<tenant>`.
