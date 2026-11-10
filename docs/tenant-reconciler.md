---
description: Stalled-tenant reconciler: minimum age, logging and completion of Creating tenants.
applies_to: src/Infrastructure/Services/DefaultConfigurationCreatorServiceStandardized.cs, src/Infrastructure/Services/TenantReconcilerOptions.cs
---

# Stalled-tenant reconciler

`DefaultConfigurationCreatorServiceStandardized` runs a durable reconciler (asset-repo wires the lifecycle
store) that claims `Creating` tenants behind a Mongo lease and re-drives `CheckSetupIdentityDataAsync`.

## Minimum age

A tenant is claimed only once its creation cycle is older than `TenantReconciler:MinimumAge`
(env `OCTO_TENANTRECONCILER__MINIMUMAGE`, default `00:00:30`, `00:00:00` disables the filter). A brand-new
tenant is still driven by the normal setup path; claiming it right away raced that path, failed on
"not seeded yet" and parked the tenant behind the 2 minute lease.

The scoped creator reads the value from `TenantReconcilerOptions.Current`, which
`AddOctoServiceInfrastructure` keeps in sync with the bound configuration.

## Logging

An `IdentityDataSeedPendingException` (the tenant has no roles yet) is logged at `Information` while the
tenant is younger than the minimum age plus one lease; later it is a `Warning` (a real stall).

## Completion

The normal setup path completes a tenant to `Active` through `MarkActiveAsync` independent of the reconcile
lease; a held lease never blocks it, and the transition clears the lease.
