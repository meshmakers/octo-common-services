# Octo Common Services

Shared infrastructure libraries, middleware and cross-cutting concerns used across the OctoMesh backend services. The repository targets .NET 10 and publishes a set of NuGet packages that wire common concerns (multi-tenancy, the distribution event hub, schema migrations, OpenTelemetry observability and OpenAPI documentation) into ASP.NET Core services through `IServiceCollection` / `IHostApplicationBuilder` extension methods.

## Published packages

- **Meshmakers.Octo.Services.Infrastructure** - infrastructure tools for OctoMesh services: `AddOctoServiceInfrastructure()` registers multi-tenancy resolution, CORS policy provider, the distribution event hub with tenant lifecycle consumers, ordered async startup initialization, the `OctoExceptionHandler`, and schema migrations (`AddMigrations`). Includes ASP.NET Core middleware for tenant resolution, authorization, cookie-based authentication and user info.
- **Meshmakers.Octo.Services.Contracts** - shared middleware contracts for OctoMesh services: distribution event hub command/request/response DTOs and broadcast messages (tenant pre/post create-update-delete, CORS client updates, blueprint lifecycle events, identity data, import/export commands) and the versioned CK method wire contract (`docs/method-wire-contract.md`).
- **Meshmakers.Octo.Services.Methods** - CK method handler SDK: typed handlers (`ICkMethodHandler<TParameters, TResult>`) on top of the method wire contract, registered with `AddCkMethodHandlers`; remote (bus) transport with announce/heartbeat/withdraw, in-process transport (`AddCkInProcessMethodHandlers`) and an invocation pipeline with authorization/audit/idempotency extension points, startup validation against the CK cache, language-neutral error mapping, redaction of sensitive parameters and opt-in validation of the forwarded caller token for remote handlers (`AddCkMethodCallerTokenValidation`) (`docs/method-handler-sdk.md`).
- **Meshmakers.Octo.Services.Notifications** - System construction kit for notifications and events; ships a service-managed CK model (published when targeting net10.0) plus Markdown-based notification rendering.
- **Meshmakers.Octo.Services.Observability** - OpenTelemetry-based observability via `IHostApplicationBuilder.AddObservability()`: ASP.NET Core and HTTP client tracing (OTLP exporter), Prometheus metrics (`/metrics` only on the internal `Observability:PrometheusPort`, off by default), resource-utilization and startup readiness health checks (`/healthz/live` and `/healthz/ready` anonymous for probes, the detailed `/health` report only with a valid Bearer token).
- **Meshmakers.Octo.Services.ArtifactStorage** - `IArtifactStore` for operational artifacts (pre-sweep dumps, tenant dumps, restore staging) with `FileSystem`, `S3` (AWS, Hetzner Object Storage, Exoscale SOS, MinIO) and `AzureBlob` providers, streamed uploads, app-side retention (`DeleteOlderThanAsync`), `ArtifactKeyBuilder` for the `<instancePrefix>/<category>/<tenantId>/<file>` layout and an optional health check. Wire it with `services.AddArtifactStorage(configuration)` (see below).
- **Meshmakers.Octo.Services.Swagger** - OpenAPI/Swagger integration via `AddOctoApiVersioningAndDocumentation()` and `UseOctoApiVersioningAndDocumentation()`: API versioning (Asp.Versioning), Swagger UI with OAuth2 + PKCE, and schema/operation transformers that surface XML documentation.

## Project structure

- `src/` - the publishable libraries listed above.
- `samples/SampleWebService` - a sample ASP.NET Core service demonstrating the packages.
- `tests/` - `Infrastructure.Tests` and supporting test assemblies.

## Artifact storage configuration

Section `ArtifactStorage` (environment `OCTO_ARTIFACTSTORAGE__*`):

| Setting | Meaning |
| --- | --- |
| `Provider` | `FileSystem` (default), `S3` or `AzureBlob` |
| `InstancePrefix` | First key segment(s), separates instances sharing a bucket (default `default`) |
| `HealthCheckWriteProbe` | Health check writes/reads/deletes a probe object instead of only listing (default `false`) |
| `FileSystem:RootPath` | Root directory (default `<temp>/octo-artifacts`); dirs `0700`, files `0600` on Unix |
| `S3:ServiceUrl`, `S3:Region`, `S3:Bucket`, `S3:ForcePathStyle` | Endpoint, signing region, bucket, path-style addressing |
| `S3:CrateDbServiceUrl` | The store endpoint as the CrateDB nodes reach it (`scheme://host:port`), when it differs from `S3:ServiceUrl`; used for the archive copy location only. Environment variable `OCTO_ARTIFACTSTORAGE__S3__CRATEDBSERVICEURL` |
| `S3:AccessKeyId`, `S3:SecretAccessKey` | Static keys; empty = AWS default credential chain |
| `S3:ServerSideEncryption` | `None` (default) or `AES256` |
| `S3:MultipartPartSizeBytes` | Multipart part size, default 16 MiB, minimum 5 MiB |
| `AzureBlob:ConnectionString` or `AzureBlob:AccountUrl` + `AccountKey` or `AccountUrl` + `UseManagedIdentity` (+ optional `ManagedIdentityClientId`) | Authentication, in that order of precedence |
| `AzureBlob:Container` | Container name |
| `AzureBlob:StagingSasLifetime` | Lifetime of the container SAS issued per archive copy run, default `02:00:00`, must be greater than zero (`OCTO_ARTIFACTSTORAGE__AZUREBLOB__STAGINGSASLIFETIME=00:30:00`) |
| `S3:CreateBucketIfNotExists`, `AzureBlob:CreateContainerIfNotExists` | Development only (default `false`) |

Credentials (`SecretAccessKey`, `AccountKey`, `ConnectionString`) never go into `appsettings*.json` or Helm values; pass them as
environment variables from a Kubernetes Secret, or use workload identity. Register the health check with
`services.AddHealthChecks().AddArtifactStorageHealthCheck()`.

### Key layout and categories

`ArtifactKeyBuilder` builds two layouts below the instance prefix:

- `<instancePrefix>/<category>/<tenantId>/<file>` (`Build`, parsed strictly by `TryParse`: exactly three segments) for the
  categories `presweep`, `tenant-dumps` and `restore-staging`.
- `<instancePrefix>/<category>/<tenantId>/<runId>/<segment>[/<segment>…]` (`BuildNested`, `RunPrefix`, `TryParseNested`) for runs
  that keep many files. Category `copy-staging` (`ArtifactCategories.CopyStaging`) holds the files of a native archive dump or
  restore (CrateDB `COPY TO` / `COPY FROM`), e.g. `main/copy-staging/sbeg/r1/<archiveRtId>/data/slice-0001/part-0.json.gz`.
  Segments follow the key rules (ASCII letters, digits, `-`, `_`, `.`, no leading `.`, so no empty, `.` or `..` segment and no
  backslash); the tenant id is lower-cased and limited to 64 characters. A nested key is not accepted by `TryParse`, a
  three-segment key is not accepted by `TryParseNested`.

A run deletes its `copy-staging/<tenant>/<run>/` folder at the end. The category needs its own lifecycle rule on the store
(expiry after one day) and an app-side `DeleteOlderThanAsync` on `<instancePrefix>/copy-staging/` as the backstop for a crashed
run. All providers list and delete nested objects by plain key prefix.

### Archive copy staging location

`IArchiveCopyStagingLocationFactory` (registered by `AddArtifactStorage`) creates the `ArchiveCopyStagingLocation` of one native
archive dump or restore run (`Meshmakers.Octo.Runtime.Contracts`, AB#5967): the location of the files as the CrateDB nodes see
them plus the `WITH` options that carry the credential, for `COPY … TO DIRECTORY` and `COPY … FROM`. The files lie under
`<instancePrefix>/copy-staging/<tenantId>/<runId>/…`; the service reads, writes and deletes them through `IArtifactStore` with the
keys of `ArtifactKeyBuilder.BuildNested`, CrateDB addresses the same objects through the location.

| Provider | Location | Credential |
| --- | --- | --- |
| `S3` | `s3://<key>:<secret>@<host>:<port>/<bucket>/<prefix>` | access key in the URI, percent-encoded; `protocol=http\|https` in `WITH` |
| `AzureBlob` | `az://<account>.<suffix>/<container>/<prefix>` | `sas_token` in `WITH`, a container SAS issued per run; never in the URI |
| `FileSystem` | not supported | the CrateDB nodes cannot reach it; dump and restore run as format 1 (rows through the service) |

- **Two endpoints (S3).** Host, port and `protocol` come from `S3:CrateDbServiceUrl` when set (the endpoint CrateDB uses, e.g.
  `http://minio.octo-infra:9000`), otherwise from `S3:ServiceUrl`. The service itself keeps using `S3:ServiceUrl`. Without any
  endpoint (AWS) the host part is omitted and CrateDB uses its default endpoint; without static keys the user info is omitted.
  A key or secret containing `:` is not supported (CrateDB splits the decoded user info at the first colon, also a
  percent-encoded one); `/`, `+` and `@` are fine. Start-up validation: `CrateDbServiceUrl` must be an absolute http or https
  URL with host and port only.
- **SAS (Azure).** The token is signed with the account key (`AzureBlob:AccountKey`, or `AccountKey` in the connection string),
  scope is the **container**: Azure cannot scope a SAS to a blob prefix without a hierarchical namespace, so the token covers the
  whole artifact container (all categories and instances that share it) for its lifetime. That is why it is issued per run, lives
  `AzureBlob:StagingSasLifetime` (default 2 h) and carries the smallest permission set: dump create/write/list, restore
  read/list. HTTPS only (plain HTTP only for an emulator endpoint), start time 5 minutes in the past against clock skew.
  A store without an account key (managed identity, connection string without `AccountKey`) is not supported yet: user delegation
  SAS is phase 2; `IsSupported` is `false` and `UnsupportedReason` names the gap.
- **No credential leaves the location.** `ToString()`, the properties, logs and exception messages of the factory show the
  redacted form only (`ArchiveCopyStagingLocation.RedactedUri`, `RedactedOptions`); the statement builder calls
  `RevealUri()` / `RevealOptions()` and keeps the result out of every log and exception, `Scrub()` removes the credential from a
  text that may echo it. The CrateDB node must not record the statement either (`stats.jobs_log_filter`, environment set-up).

The contract tests in `tests/ArtifactStorage.Tests` run against the file system and, when Docker is available, against MinIO
(`OCTO_TEST_MINIO_IMAGE`, default `cgr.dev/chainguard/minio:latest`) and Azurite containers; the staging location is tested against
Azurite (the SAS is accepted and refused as designed) and against CrateDB plus MinIO on one Docker network
(`OCTO_TEST_CRATEDB_IMAGE`, default `crate:6.4.6`: `COPY TO` / `COPY FROM` with a generated location, once with a store without a site region and once with MinIO in region `at-vie-1`, which sends `x-amz-bucket-region` like Exoscale SOS; CrateDB 5.x fails the second one, AB#7059). Without Docker, or with
`OCTO_SKIP_CONTAINER_TESTS=true`, the container tests are reported as skipped.

## Build

```bash
dotnet build Octo.Common.Services.sln
```

## Test

```bash
dotnet test Octo.Common.Services.sln
```

## Documentation

The complete OctoMesh documentation is available at https://docs.meshmakers.cloud.

## License

Released under the MIT License.
