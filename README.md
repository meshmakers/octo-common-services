# Octo Common Services

Shared infrastructure libraries, middleware and cross-cutting concerns used across the OctoMesh backend services. The repository targets .NET 10 and publishes a set of NuGet packages that wire common concerns (multi-tenancy, the distribution event hub, schema migrations, OpenTelemetry observability and OpenAPI documentation) into ASP.NET Core services through `IServiceCollection` / `IHostApplicationBuilder` extension methods.

## Published packages

- **Meshmakers.Octo.Services.Infrastructure** - infrastructure tools for OctoMesh services: `AddOctoServiceInfrastructure()` registers multi-tenancy resolution, CORS policy provider, the distribution event hub with tenant lifecycle consumers, ordered async startup initialization, the `OctoExceptionHandler`, and schema migrations (`AddMigrations`). Includes ASP.NET Core middleware for tenant resolution, authorization, cookie-based authentication and user info.
- **Meshmakers.Octo.Services.Contracts** - shared middleware contracts for OctoMesh services: distribution event hub command/request/response DTOs and broadcast messages (tenant pre/post create-update-delete, CORS client updates, blueprint lifecycle events, identity data, import/export commands).
- **Meshmakers.Octo.Services.Notifications** - System construction kit for notifications and events; ships a service-managed CK model (published when targeting net10.0) plus Markdown-based notification rendering.
- **Meshmakers.Octo.Services.Observability** - OpenTelemetry-based observability via `IHostApplicationBuilder.AddObservability()`: ASP.NET Core and HTTP client tracing (OTLP exporter), Prometheus metrics, resource-utilization and startup readiness health checks.
- **Meshmakers.Octo.Services.Swagger** - OpenAPI/Swagger integration via `AddOctoApiVersioningAndDocumentation()` and `UseOctoApiVersioningAndDocumentation()`: API versioning (Asp.Versioning), Swagger UI with OAuth2 + PKCE, and schema/operation transformers that surface XML documentation.

## Project structure

- `src/` - the five publishable libraries listed above.
- `samples/SampleWebService` - a sample ASP.NET Core service demonstrating the packages.
- `tests/` - `Infrastructure.Tests` and supporting test assemblies.

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
