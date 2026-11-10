---
description: System.Notification CK model: ownership markers, subscriptions, event category and details, explicit publishing.
applies_to: src/Notifications/**, tests/Notifications.Tests/**, tests/Notifications.IntegrationTests/**, azure-pipelines.yml
---

# System.Notification CK model

## System.Notification CK model: ownership markers are enforced

`src/Notifications/Notifications.csproj` sets `OctoEnforceRuntimeStateMarkers=true`: the `CkLintRuntimeStateMarkers` task (CK
MsBuildTasks package) fails the build with `OCTO-CK001` when an attribute in `ConstructionKit/attributes/*.yaml` declares no `ownership`
(or the deprecated `isRuntimeState`). The marker change restated the existing behaviour: 8 attributes got `ownership: SeedOwned`; the five
`TenantOwned` attributes (mail switch, redirect URL, template subject/body/rendering) are unchanged. Same resolved ownership,
so `octo-ckc ValidateVersion` reported "no bump required" and System.Notification stayed 2.4.0; it was later re-pinned to 2.5.0 after the range-retention flip and raised to 2.6.0 for subscriptions.
**Review question for every new attribute:** could an operator or user type this value in the product? Yes → `TenantOwned` (or `Secret`
for a credential); a service writes it → `RuntimeState`; the product ships and corrects it → `SeedOwned`.

## System.Notification 2.6.0: notification subscriptions

Three types and four associations (`types/notificationSubscription.yaml`, `notificationPreference.yaml`,
`notificationDeliveryState.yaml`, `associations/subscription-associations.yaml`, attributes in `attributes/subscription-attributes.yaml`):

| Type | Holds | Written by |
|---|---|---|
| `NotificationSubscription` | what to send: `SubscriptionQuery` (a SimpleRtQuery), `SubscriptionTemplate`, `Enabled` (= Deployed), `DeployedAt` | Studio; `Enabled` / `DeployedAt` only by the communication controller's deploy / undeploy |
| `NotificationPreference` | one recipient: `SubjectId` (token `sub`), `CronExpression`, `TimeZone`, `SkipExistingRows` (Onwards / Catch up), `PreferenceSubscription` | Studio |
| `NotificationDeliveryState` | the engine's bookkeeping for one preference (`DeliveryStatePreference`): `ProcessedUntil` (the latest change handled), `SentRows` (record `SentRow`: `RowRtId` / `SentAt` / `ChangedAt`, kept 30 days and at most 10 000, the adapter prunes it), `QueryRtId`, `DeliveredToSubjectId` / `DeliveredWithSkipExistingRows` (property names `SubjectId` / `SkipExistingRows`: which recipient and start option the rows belong to), `LastRunAt`, `LastSentAt`, `LastError`, `RunningSince` | mesh adapter (`PrepareNotification@1` / `CommitNotificationState@1`); the communication controller only erases a state whose preference is gone |

- **Deliberate exceptions to the review question above.** `DeployedAt` and the delivery state's `ProcessedUntil`,
  `SentRows`, `QueryRtId`, the two `Delivered*` attributes, `LastRunAt` and `LastSentAt` are written by services but are `TenantOwned`, not `RuntimeState`:
  `RuntimeState` is left out of an `ExportRt`, and a tenant moved without them would mail every recipient rows they already got
  (and treat every Onwards start as new). Only the lease (`RunningSince`) and `LastError` are instance-local `RuntimeState`.
- `QueueNames.NotificationScheduleChannelName` is the prefix of the engine's tick queue
  (`{prefix}-{tenantId}-{pipelineRtId}`, lower-cased); the controller sends to it and the adapter's `FromNotificationSchedule@1`
  listens on it, so it is a cross-repo contract (pinned in the adapter by `FromNotificationScheduleNodeTests`). The message itself,
  `NotificationScheduleTick`, is pinned by `NotificationScheduleTickWireContractTests`.

## System.Notification 2.7.0: event log category and structured details

Changelog of the model (Minor, `octo-ckc ValidateVersion` against the published 2.6.0: VALID, required level MINOR): the `Event`
type (and so `StatefulEvent`) gets two optional attributes, nothing that exists changes. No CK model depends on System.Notification;
the blueprints `System.Notification.Bootstrap`, `Samples.EnergyEnvironment` (`[2.1.0,3.0)`) and `System.Communication.Notifications`
(`[2.6.0,3.0)`) accept 2.7.0, so nothing cascades. An older service keeps writing events without the new fields (embedded-import
downgrade guard).

| Attribute (type) | Meaning |
|------------------|---------|
| `Category` (`EventCategory`, String, optional) | stable dotted kind of the event, e.g. `Method.Invocation`, `Archive.Transition`; the auditor's filter |
| `Details` (`EventDetails`, record list of `EventDetail`, optional) | structured key/value strings (`Key`, `Value`); e.g. invocation id, method, outcome |

Both are `SeedOwned` like the other `Event` attributes (an event travels with an `ExportRt`; `RuntimeState` would drop it).

- `IEventRepository` has overloads `StoreEventAsync` / `StoreSystemEventAsync` with `category` and `details`
  (`IReadOnlyCollection<KeyValuePair<string,string>>`); the older overloads delegate with `null`, so existing callers and
  events without a category are unchanged. `IEventRepository` has a single implementation (`EventRepository`).
- `EventRepositoryAuditEventSink` writes `AuditEvent.Category` into `Category` and `AuditEvent.Metadata` into `Details`
  (`MapMetadata`): `null` values are skipped, values are rendered with the invariant culture (dates ISO 8601 round-trip, lists
  comma-separated), an empty result writes no `Details`. The sink still catches everything and logs; it never fails the caller.
- Tests: `Notifications.Tests/EventRepositoryAuditEventSinkTests` (mapping, culture, never-throws) and
  `Notifications.IntegrationTests` (real MongoDB replica set via Testcontainers: tenant and system tenant, an event without
  category round-trips, filter by `Category`). The integration project is skipped without Docker or with `OCTO_SKIP_CONTAINER_TESTS=true`.
- **Producer inventory (state of origin/main, 2026-10-30).** Metadata is persisted verbatim, so every `new AuditEvent(...)` was
  checked: none carries a credential, token or parameter value; the keys are identifiers, names, counts and timestamps. A new
  producer (method invocations, refused method calls) must keep it that way and never put parameter values or tokens into `Metadata`.

| Category | Producer | Metadata keys |
|----------|----------|---------------|
| `CkModelImport.ExtensibleEnumOverride` | engine `ForwardingCkModelImportAuditTrail` | `ckModelId`, `ckEnumId`, `ckEnumValueName`, `extensionValueName`, `extensionValueKey` |
| `CkModelMigration.WrapScalarInRecord` | engine `ForwardingCkModelImportAuditTrail` | `stepId`, `ckTypeId`, `rtId`, `sourceAttribute`, `targetRecordCkRecordId`, `wrappedCount` |
| `RtImport.MissingMandatoryAttributes` | engine `ForwardingRtImportAuditTrail` | `ckTypeId`, `rtId`, `missingCkAttributeIds` (attribute ids) |
| `RtImport.SkippedDanglingEdges` | engine `ForwardingRtImportAuditTrail` | `skippedCount`, `sampleEdges` (edge descriptions: role, type and rtIds) |
| `RtImport.ReplacedToOneEdges` | engine `ForwardingRtImportAuditTrail` | `replacedCount`, `sampleEdges` |
| `RtImport.BlueprintAttributesStripped` | engine `ImportBlueprintLockEnforcer` | `strippedAttributeCount`, `strippedEntityCount` |
| `Archive.Transition` | engine `ForwardingArchiveAuditTrail` | `archiveRtId`, `fromStatus`, `toStatus`, `reason` |
| `Archive.Deletion` | engine `ForwardingArchiveAuditTrail` | `archiveRtId`, `statusAtDeletion` |
| `Archive.RollupRun` | engine `ForwardingArchiveAuditTrail` | `rollupRtId`, `bucketStart`, `bucketEnd`, `rowsWritten`, `elapsedMs` |
| `Archive.RollupFreeze` | engine `ForwardingArchiveAuditTrail` | `rollupRtId`, `frozenUntil`, `reason` |
| `Archive.Recompute` | engine `ForwardingArchiveAuditTrail` | `archiveRtId`, `rangeStart`, `rangeEnd`, `rowsProcessed`, `windowsProcessed`, `elapsedMs` |
| `Archive.RecomputeFailure` | engine `ForwardingArchiveAuditTrail` | `archiveRtId`, `rangeStart`, `rangeEnd`, `reason` |
| `Archive.RetroReachCapped` | engine `ForwardingArchiveAuditTrail` | `archiveRtId`, `consumedWatermark`, `cappedFloor`, `capMs` |
| `Tenant.CreateFailed` | engine-mongodb `TenantContext` | `tenantId`, `databaseName`, `correlationId` |
| `DataPermissions.WriteViolation` / `ReadViolation` / `BlueprintLockViolation` | engine `DataPermissionRules`, `RtDataPermissionEvaluator`, `DataPermissionWriteGuard`, `ImportBlueprintLockEnforcer`; engine-mongodb `TenantRepository` | none (the message names subject and type) |

## Publishing the system CK model: automatic from main

A merge to `main` publishes `System.Notification` to the private CK catalog automatically (default since 2026-11-10). The pipeline
parameter `publishSystemCkModels` (boolean, **default true**; the gate was introduced as default false) feeds
`publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0` and later; pinned: `tpl-v1.11.0`). ValidateVersion, the schema compile and the cascade check
run on every build and refuse a breaking change declared as minor; an already-published version is a no-op. Default true because CK v2
range retention and the completed re-pin removed the exact-pin breakage that motivated default false.

**To build main without publishing:** Azure DevOps -> pipeline `octo-common-services-CI` -> *Run pipeline* -> branch `main` -> untick
*Publish System CK models to the CK catalogs (main)*; CLI: `az pipelines run --id 90 --branch main --parameters publishSystemCkModels=false`.
`r*` tags (release train) publish as before; `dev/*`/`test/*` builds never publish to the shared catalogs.
