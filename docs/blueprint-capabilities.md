---
description: Capabilities a blueprint needs (Communication, AI) are switched on by the platform; the reconciler, its invariant and the optional CkModelDependencies of the blueprint events.
applies_to: src/Infrastructure/Blueprints/**, tests/Infrastructure.Tests/Blueprints/**, src/Contracts/DistributionEventHub/Messages/BlueprintApplied.cs, src/Contracts/DistributionEventHub/Messages/BlueprintUpdated.cs
---

# Capabilities enabled by a blueprint

A tenant that gets a blueprint which needs the Communication Controller or the AI service (a tenant created with the
blueprint, `InstallBlueprint`, a force re-apply or `UpdateBlueprint`) has these capabilities switched on by the platform.
`EnableCommunication` / `EnableAi` are needed only for tenants without such a blueprint, and after a manual disable when
no blueprint operation follows.

The blueprint stays declarative: it says nothing about capabilities. What it needs follows from its CK model dependencies,
and each service reconciles what it owns when it receives the blueprint events of the distribution event hub.

## The events carry the CK model dependencies

`BlueprintApplied` and `BlueprintUpdated` (`src/Contracts/DistributionEventHub/Messages/`) have the optional, init-only
property `CkModelDependencies`: the names of the CK models the blueprint depends on, distinct and sorted ordinally, as the
blueprint engine computed them.

- `BlueprintApplied`: the CK model dependencies of the applied blueprint and of every blueprint the apply resolved for it
  (dependencies the tenant already runs in a newer version included).
- `BlueprintUpdated`: the CK model dependencies of the target version.
- `null`: the publisher does not report them (older versions). Consumers must then do nothing.

Compatibility: the positional constructor and `Deconstruct` are unchanged, an older consumer ignores the JSON property
and a message of an older publisher reads as `null` (`tests/Contracts.Tests/DistributionEventHub/`).

Why the event and not the tenant: the services cannot recover the dependencies themselves. The installation rows of a
tenant store no CK dependencies, the services hold only their embedded blueprint catalogs, and the CK models a tenant
holds are no signal - `System.Communication` is imported into every tenant.

## Which dependency needs which capability

`BlueprintCapabilityRequirements`: `System.Communication` needs Communication; `System.Ai` needs AI and Communication
(System.Ai depends on System.Communication). Names match exactly and case-sensitively: `System.Communication.Extra`,
`MySystem.Ai` or `System.Ai.Builder` need nothing.

Known limitation (accepted): CK model dependencies of CK models are not followed. A blueprint whose own CK model depends
on `System.Ai` or `System.Communication` gets nothing enabled unless the blueprint declares `System.Ai` /
`System.Communication` directly in its `ckModelDependencies` - a starter blueprint that needs a capability declares the
model directly.

## The reconciler

`IBlueprintCapabilityReconciler.ReconcileAsync(request, capability)` (`BlueprintCapabilityReconciler`, registered with
`services.AddBlueprintCapabilityReconciler(configuration)`, scoped) enables the capability of the hosting service through
its `IConfigurationService`. The host must also register the blueprint installation store (`AddMongoBlueprintSupport`).

Invariant - the capability is enabled only

1. for the tenant the event names, after it resolves to an existing tenant (`ISystemContext.TryFindTenantRepositoryAsync`,
   which normalises the id) and that tenant is not the system tenant;
2. when the blueprint the event names (`Name-Version`) is installed in that tenant in exactly that version
   (`ITenantBlueprintInstallations`);
3. when the reported CK model dependencies need it; no dependencies reported = nothing (fail closed);
4. for AI only after Communication is enabled for the tenant (the Communication flag the Communication Controller
   writes); it never enables Communication itself.

It never disables anything and never throws: a refusal is logged as a warning, a failed enable as an error with tenant
and cause; the blueprint operation is already committed and stays as it is. An enabled capability is left alone
(idempotent), also when another replica of the broadcast consumer won the race. Every automatic enable is logged at
Information level with structured fields (tenant, blueprint id with version, capability, trigger event, correlation id);
a warning on every tenant created with a starter blueprint would be noise. Only a failure to enable is a warning (refused,
prerequisite missing) or an error. When the enable throws but the flag reads enabled afterwards (another replica won, or
the flag was written and a later step such as the tenant start failed), the outcome is "already enabled" and the cause is
logged as a warning with the exception.

Re-enable after a manual disable (decided): an install, force re-apply or update of a blueprint that still needs the
capability enables it again. All three are explicit actions of a caller allowed to change the data model; remembering a
"disabled by hand" state would need new tenant state. Such a re-enable cannot be detected as one and is logged like every
other automatic enable (Information). An update whose target no longer needs the capability, or any other
event, never enables, and an uninstall never disables.

Only the success events are consumed: a failed operation (`BlueprintOperationFailed`) enables nothing.

Residual risk: a party that can publish on the distribution event hub can claim a dependency for a blueprint that is
really installed in the tenant. The hub is service-internal; the installation check limits it to installed blueprints.

## Timing: tenant created with a blueprint

A tenant created with a blueprint publishes `BlueprintApplied` from inside the create, before the tenant registry
commits. The reconciler therefore looks the tenant and the installation row up several times
(`BlueprintCapabilityOptions.LookupAttempts`, default 12, `LookupDelay` 5 s) before it refuses the event. A create that
fails after the apply rolls the registry back; the tenant never resolves and nothing is enabled. A version different from
the event's is refused at once: the row is written before the event is published.

AI waits for Communication (`PrerequisiteAttempts`, default 36, `PrerequisiteDelay` 5 s), then gives up with a warning
that names the tenant and the commands to run.

A refused event therefore holds the consumer for up to `LookupAttempts` x `LookupDelay` (60 s by default), and the AI
path up to `PrerequisiteAttempts` x `PrerequisiteDelay` more (about 4 minutes in total); in the Communication Controller
the sweep of that event waits as long. A negative delay counts as zero, attempts below 1 count as 1.

Options section: `BlueprintCapabilities` (e.g. `OCTO_BlueprintCapabilities__LookupAttempts`).

## Seed data that points at what the capability seeds

Enabling Communication applies the service-managed `System.Communication` blueprint, whose seed brings the Mesh Adapter
(`System.Communication/Adapter` `670000000000000000000002`). A blueprint whose seed links its pipelines to that adapter
(`System.Communication/Executes`) is applied **before** the platform enables Communication: its seed runs, the asset
repository publishes `BlueprintApplied`, and only then does the Communication Controller enable the capability. The
adapter therefore does not exist yet when the seed is imported (AB#9651, found on test-2: "Skipped 3 association
edge(s) ... missing target System.Communication/Adapter@670000000000000000000002").

Mechanism (decided in AB#9651): the blueprint engine keeps such edges instead of losing them, and writes them once both
ends exist. The install result reports them as a warning (message 73); the `System.Communication` seed of the enable
writes them (message 74, logged by the Communication Controller as "Wrote <n> deferred seed association edge(s) ...").
The reconciler and the order of the events are unchanged: nothing waits, and the blueprint operation never blocks on a
capability. The same holds when Communication is enabled by hand later or stays disabled after a manual disable (the
edges wait in the tenant). Rules (to-one roles, identity roles, uninstall, limits): octo-construction-kit-engine
`docs/blueprints.md`, "Associations to entities the tenant does not hold yet". The asset repository and the Communication
Controller need an engine with the change; until then the edges are dropped as before and `EnableCommunication` before
the install is the workaround.

Chosen over enabling the capability before the seed: the blueprint engine runs in the asset repository and cannot switch
on another service's capability without a call to it (and without the toggle policy the reconciler deliberately avoids,
AB#9644); waiting for the asynchronous enable inside the install would hold the install up to the reconciler's lookup
time and still lose the edges when the enable fails.

## Consumers

- octo-communication-controller-services: `BlueprintAppliedConsumer` (Communication), before its reconcile sweep.
- octo-ai-services: `BlueprintCapabilityConsumer` (AI).

Tests: `tests/Infrastructure.Tests/Blueprints/BlueprintCapabilityReconcilerTests.cs` (one test per abuse case: other
tenant, missing dependency, re-enable after disable, forged or replayed event, look-alike names, system tenant, AI without
Communication, event without dependencies).
