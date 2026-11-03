---
description: Artifact store: nested keys, copy-staging category and the archive copy staging location for S3 and Azure.
applies_to: src/ArtifactStorage/**, tests/ArtifactStorage.Tests/**
---

# Artifact storage

## Artifact store: nested keys and the archive copy staging location (AB#5967 / AB#6969)

`src/ArtifactStorage` (package `Meshmakers.Octo.Services.ArtifactStorage`, docs: `README.md`, "Artifact storage configuration" and
"Key layout and categories") is the platform artifact store. Native archive dump and restore (CrateDB `COPY TO` / `COPY FROM`,
concept `octo-construction-kit-engine/docs/concept-tenant-backup-native-archive-dump.md`) use it as the staging area:

- **Nested keys.** `ArtifactKeyBuilder.BuildNested` / `RunPrefix` / `TryParseNested` build and parse
  `<instancePrefix>/<category>/<tenantId>/<runId>/<segment>[/…]`; category `ArtifactCategories.CopyStaging` (`copy-staging`).
  The strict three-segment `TryParse` is unchanged and rejects a nested key. The providers need no change: they list, delete and
  expire by plain key prefix (`ArtifactStoreContractTests.NestedKeys_*` runs against file system, MinIO and Azurite). The category
  needs a lifecycle rule on each store (expiry one day); that is environment set-up, not code of this repo.
- **Staging location.** `IArchiveCopyStagingLocationFactory` (`Staging/`) creates `ArchiveCopyStagingLocation` (type and
  redaction live in the engine, `Runtime.Contracts/StreamData`) for S3 and Azure; FileSystem and an Azure store without account key
  report `IsSupported = false` with a reason, `Create` then throws `ArchiveCopyStagingNotSupportedException`. The project
  references `Meshmakers.Octo.Runtime.Contracts` for the type (open design question: a neutral location type would avoid the
  dependency on the large package; the engine contract was the lane's input).
- **Options.** `S3:CrateDbServiceUrl` (`OCTO_ARTIFACTSTORAGE__S3__CRATEDBSERVICEURL`, the second endpoint as CrateDB sees it) and
  `AzureBlob:StagingSasLifetime` (default 2 h). Validation names the option, never a value.
- **S3 secret with `:`.** Verified against CrateDB 5.9.10 + MinIO: `/`, `+`, `@` survive percent-encoding, a `:` does not
  (`SignatureDoesNotMatch`: CrateDB splits the decoded user info at the first colon). The factory reports such keys as not
  supported instead of letting `COPY` fail with a signature error.
- **Azure SAS.** Container scope, per run, `Staging/` + `AzureBlob/AzureStagingSas.cs`; dump create/write/list, restore read/list;
  managed identity = phase 2. Azurite proves accepted and refused operations (`ArchiveCopyStagingAzuriteTests`).
- **Canaries.** `ArchiveCopyStagingLocationFactoryTests` assert that key, secret (every spelling) and `sas_token` appear in no
  `ToString()`, property, JSON serialization, `Scrub`bed text, factory log line or exception message. Proven red by mutation: the
  factory logging `RevealUri()`, the secret or the token placed in the URI path, a connection string echoed into the reason.
  Never log or serialize `RevealUri()` / `RevealOptions()` in new code.
