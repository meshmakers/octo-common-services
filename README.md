# Octo Common Services

Shared infrastructure libraries, middleware and cross-cutting concerns used across the OctoMesh backend services. The repository targets .NET 10 and publishes a set of NuGet packages that wire common concerns (multi-tenancy, the distribution event hub, schema migrations, OpenTelemetry observability and OpenAPI documentation) into ASP.NET Core services through `IServiceCollection` / `IHostApplicationBuilder` extension methods.

## Published packages

- **Meshmakers.Octo.Services.Infrastructure** - infrastructure tools for OctoMesh services: `AddOctoServiceInfrastructure()` registers multi-tenancy resolution, CORS policy provider, the distribution event hub with tenant lifecycle consumers, ordered async startup initialization, the `OctoExceptionHandler`, and schema migrations (`AddMigrations`). Includes ASP.NET Core middleware for tenant resolution, authorization, cookie-based authentication and user info.
- **Meshmakers.Octo.Services.Contracts** - shared middleware contracts for OctoMesh services: distribution event hub command/request/response DTOs and broadcast messages (tenant pre/post create-update-delete, CORS client updates, blueprint lifecycle events, identity data, import/export commands).
- **Meshmakers.Octo.Services.Notifications** - System construction kit for notifications and events; ships a service-managed CK model (published when targeting net10.0) plus Markdown-based notification rendering.
- **Meshmakers.Octo.Services.Observability** - OpenTelemetry-based observability via `IHostApplicationBuilder.AddObservability()`: ASP.NET Core and HTTP client tracing (OTLP exporter), Prometheus metrics, resource-utilization and startup readiness health checks.
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
| `S3:AccessKeyId`, `S3:SecretAccessKey` | Static keys; empty = AWS default credential chain |
| `S3:ServerSideEncryption` | `None` (default) or `AES256` |
| `S3:MultipartPartSizeBytes` | Multipart part size, default 16 MiB, minimum 5 MiB |
| `AzureBlob:ConnectionString` or `AzureBlob:AccountUrl` + `AccountKey` or `AccountUrl` + `UseManagedIdentity` (+ optional `ManagedIdentityClientId`) | Authentication, in that order of precedence |
| `AzureBlob:Container` | Container name |
| `S3:CreateBucketIfNotExists`, `AzureBlob:CreateContainerIfNotExists` | Development only (default `false`) |

Credentials (`SecretAccessKey`, `AccountKey`, `ConnectionString`) never go into `appsettings*.json` or Helm values; pass them as
environment variables from a Kubernetes Secret, or use workload identity. Register the health check with
`services.AddHealthChecks().AddArtifactStorageHealthCheck()`.

The contract tests in `tests/ArtifactStorage.Tests` run against the file system and, when Docker is available, against MinIO
(`OCTO_TEST_MINIO_IMAGE`, default `cgr.dev/chainguard/minio:latest`) and Azurite containers. Without Docker, or with
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
