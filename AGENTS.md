# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before
changing the code it covers.

## What this repo is

Shared .NET infrastructure libraries used across all OctoMesh backend services. See `README.md`
for the published package list. Targets .NET 10, DebugL for local development.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/ArtifactStorage/**`, `tests/ArtifactStorage.Tests/**` | `docs/artifact-storage.md` |
| `src/Observability/**`, `src/Infrastructure/Routing/**`, `src/Infrastructure/Cors/**`, `src/**/TenantMiddleware.cs`, `src/Infrastructure/Middleware/Cookie*.cs` | `docs/http-pipeline.md` |
| `src/Methods/Audit/**` | `docs/method-audit.md` |
| `src/Methods/Dispatching/**` | `docs/method-dispatcher.md` |
| `src/Methods/Handlers/**`, `src/Methods/Typed/**`, `src/Methods/Configuration/**` | `docs/method-host.md` |
| `src/Methods/Metrics/**`, `src/Observability/ObservabilityBuilder.cs` | `docs/method-metrics.md` |
| `src/Methods/InProcess/**`, `src/Methods/Authorization/**`, `src/Methods/Redaction/**` | `docs/method-pipeline.md` |
| `src/Contracts/DistributionEventHub/Methods/**`, `tests/Contracts.Tests/Methods/**`, `src/Methods/Idempotency/**` | `docs/method-wire-contract.md` |
| `src/Notifications/**`, `tests/Notifications.Tests/**`, `tests/Notifications.IntegrationTests/**`, `azure-pipelines.yml` | `docs/notification-ck-model.md` |
| `src/Infrastructure/**/TenantAuthorization*.cs`, `src/Infrastructure/Authorization/**`, `src/Infrastructure/Services/*TenantHierarchyReader.cs` | `docs/tenant-authorization.md` |
| `src/Infrastructure/Services/DefaultConfigurationCreatorServiceStandardized.cs`, `src/Infrastructure/Services/TenantReconcilerOptions.cs` | `docs/tenant-reconciler.md` |
| `src/Infrastructure/Services/**`, `src/Infrastructure/Consumers/**`, `src/Infrastructure/Initialization/**`, `src/Contracts/DistributionEventHub/Commands/**` | `docs/tenant-setup.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/method-handler-sdk.md` (developer guide with a working sample),
`docs/method-performance.md` (replica sharing, dispatch overhead and redelivery measurements, with the
repeat commands), `docs/method-adversarial-suite.md` (attack cases, results, findings) and
`docs/concepts/tenant-provisioning-lifecycle.md` (concept of durable tenant provisioning).

## Build & test

```bash
dotnet build Octo.Common.Services.sln -c DebugL
dotnet test  Octo.Common.Services.sln -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Versions come only from the pipeline: `Directory.Build.props` has no release fallback, so a
Debug/Release build without `-p:OctoVersion=X.Y.Z` (or `-p:OctoNugetPrivateServer=<feed>` for the main
line `0.1.*`) fails fast with MSBuild error `OCTO0001`. Use `-c DebugL` locally. Every project in
`Octo.Common.Services.sln` must map the solution configuration `DebugL` to the project configuration
`DebugL`: `TestAssemblyCorrectMigrations` was mapped to `Debug` and so restored `3.4.*` packages from
nuget.org inside a DebugL build, which only surfaced once the fallback was gone.

Propagate the produced `Meshmakers.Octo.Services.*.999.0.0.nupkg` files from `bin/DebugL/` into
`../nuget` so downstream services (Asset-Repo, Reporting, Identity, ...) pick up local changes.

### Tests

`tests/Infrastructure.Tests` — xUnit + FakeItEasy. `Infrastructure.csproj` exposes internals to this
assembly via `<InternalsVisibleTo Include="Infrastructure.Tests" />` so internal middleware such as
`TenantMiddleware` can be unit-tested directly (see `Middleware/TenantMiddlewareTests.cs`).

## Before you commit

1. `dotnet build Octo.Common.Services.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true` in `Directory.Build.props`).
2. The test command above reports 0 failed. `tests/Methods.IntegrationTests` starts a private RabbitMQ
   container and needs Docker.
3. Changed a method wire contract type: refresh the golden files and review the diff
   (see `docs/method-wire-contract.md`).
4. Changed a method path (filter, transport, handler host): add its cases to the adversarial suite
   (`docs/method-adversarial-suite.md`) before it ships.
5. Update the doc in `docs/` that covers the code you changed, in the same commit.
6. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets
  in code comments, docs or test names.
- **Do not map `/metrics` on the main listener** and do not make `/health` anonymous
  (`docs/http-pipeline.md`).
- **One `TenantIdRouteConstraint`.** Do not add a local copy to a host (`docs/http-pipeline.md`).
- **Tenant authorization is cross-repo.** Every host calls both `UseOctoTenantAuthorization()` and
  `AddOctoTenantAuthorization(builder.Configuration)`; one configurator owns the JWT bearer scheme and
  `AddJwtBearer()` is called without an argument (`docs/tenant-authorization.md`).
- **Never log a `DistClientDto`, a `CkMethodRequest` payload, a caller token, a method result or a staging
  location's `RevealUri()`.** Keep the redacting `ToString()` overrides when you add members.
- **Every CK attribute declares `ownership`** (`OCTO-CK001`, `docs/notification-ck-model.md`).
- **A merge to `main` does not publish System.Notification.** Publishing is an explicit pipeline run
  (`docs/notification-ck-model.md`).

## Cache streams: missing file is `null`

`OctoRepository` (`src/Infrastructure/DistributionEventHub`, the `IRepository` of `IDistributedCacheService`) returns
`null` from both `DownloadBinaryAsync` overloads when the cache file does not exist (it used to leak the repository's
`EntityNotFoundException` for `IDownloadStreamHandler`). `DistributedCacheService.GetCacheStreamByIdAsync` therefore
returns `null`, and callers such as the `octo-bot-services` import jobs raise a typed error naming tenant and key.
The transaction is aborted on a miss. Read-your-writes of the underlying GridFS read lives in
`octo-construction-kit-engine-mongodb` (primary read on a miss).
