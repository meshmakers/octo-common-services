# Changelog

All notable changes to the `Meshmakers.Octo.Services.*` packages. Versions come from the pipeline (AB#6291), so
entries are grouped by work item until a release assigns the version.

## Unreleased

### Added

- **AB#8421** — new package `Meshmakers.Octo.Services.RestDeprecation`: `[OctoRestDeprecated(Since, Sunset, Link)]`
  marks a REST action (or controller) deprecated. Every response of a marked endpoint, 4xx/5xx included, carries
  `Deprecation: @<unix seconds>` (RFC 9745), `Sunset: <IMF-fixdate>` (RFC 8594) and
  `Link: <url>; rel="deprecation"; type="text/html"` (plus `Link: <url>; rel="successor-version"`, RFC 5829, with the
  optional `Successor`); the OpenAPI operation is `deprecated: true` with the sunset date
  and link in its description; the counter `octo.rest.deprecated.calls` (meter `Meshmakers.Octo.RestDeprecation`,
  labels `endpoint`, `client_id`, `status_class`; pipeline service accounts `octo-pipeline-sa-*` reported as
  `octo-pipeline-sa`, never with the tenant) counts the remaining calls. Invalid attribute values fail the service
  at startup, naming the action. `AddObservability()` registers the mechanism and exports the meter;
  `AddOctoApiVersioningAndDocumentation()` adds the OpenAPI transformer. Unmarked endpoints are unchanged. See
  `docs/rest-deprecation.md`.
