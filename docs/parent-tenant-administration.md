---
background: true
description: Parent-tenant administration beyond REST endpoints: the closed CK method list, the per-request predicate of the tenant gate, logs and tests.
---

# Parent-tenant administration of CK methods and shared endpoints

The REST rule (`[AllowParentTenantAdministration]`, `docs/tenant-authorization.md`) lets a **user** token of a parent tenant
administer a registered child tenant on marked endpoints. The same administration operations exist as CK methods, called
through an endpoint that also serves tenant content (the asset repository's GraphQL endpoint). This doc describes how the
rule reaches them without widening anything.

## The closed method list and the caller-tenant rule

The rule (`src/Methods/Authorization/CkParentTenantAdministrationMethods.cs`,
`CkMethodCallerTenantRule.EvaluateAsync`, `CkMethodCallerTenantCheck.CheckAsync`, tests
`tests/Methods.Tests/Authorization/CkParentTenantAdministrationTests.cs` and the REST-vs-method parity matrix
`ParentTenantAdministrationParityTests.cs`): the method counterpart of the REST rule of `[AllowParentTenantAdministration]`
(`tenant-authorization.md`). A **user** token (`sub`) issued for tenant P may call a method of the **closed list**
`CkParentTenantAdministrationMethods` (`System.Bot/TenantRepository.Dump-1`, `.Restore-1`, `.ExportArchiveData-1`,
`.ImportArchiveData-1`; ordinal match, a new method version is not on it) on tenant C when
`ITenantHierarchyReader.IsChildTenantAsync(P, C)` is true: one level for an ordinary parent, the whole estate for the system tenant,
cached and fail-closed (no reader registered, or a reader that throws = refused). The opt-in lives at the method, never on an
endpoint; every other method keeps the exact match. Service tokens never use it (exact match plus `CrossTenantServiceClientIds`
as before). The roles stay those the method declares (`TenantManagement`, the equivalent of the REST policy) and are judged on
the caller's token by the gateway and the host. The tenant generation step checks `tenant_gen` against the **token tenant P**, so a
P token from before P was recreated is refused on this path as on the own-tenant path. Always enforcing, no switch: the
`LogOnly`/`Disabled` modes of the HTTP side never apply here. Where it runs: the asset-repo gateway
(`CheckAsync`, transport label `gateway`), the in-process filter (`CkMethodCallerTenantFilter`, optional `ITenantHierarchyReader`)
and the bus host (`ICkMethodCallerTokenValidator.ValidateForMethodAsync`, called by the handler host through the
`ValidateAsync(CkMethodRequest)` extension; the default interface implementation has **no** parent rule, so another implementation
stays on the exact match). The synchronous `CkMethodCallerTenantCheck.IsRefused` and `ValidateAsync(token, tenant)` without a
method keep the exact match. Every grant is logged at Information with subject, client, token tenant, call tenant, method id and the
place of the check; never the token or the parameters. Operation records of these methods are readable by the parent through
the asset repository's `runtime.operations` (records of the closed list only); `CkMethodOperationQuery.MethodIds` restricts a
store query to a method set (an empty set matches nothing).

## The per-request predicate of the tenant gate

A GraphQL endpoint serves the child's data **and**, through CK methods, the same administration operations as the REST
jobs routes (backup, restore, archive data export/import). It must never carry the endpoint marker, so it carries the
endpoint metadata `IAllowParentTenantAdministrationRequest` instead (`src/Infrastructure/Authorization/`): a predicate
that classifies **one request**. The middleware applies the parent-tenant rule to a user token of another tenant only
when the endpoint has no marker, the predicate answers `true` **and** `ITenantHierarchyReader` confirms the child — the
same hierarchy lookup, cache, reach and grant log as above, counted as `allowed_parent_admin`. A request the predicate
does not classify keeps the exact match (and gets the ordinary denial message); a classified request on a tenant that is
not a child gets the combined "not an existing child" message. The predicate is asked only after the equality check and
only for user tokens with a `tenant_id`; service tokens never reach it. A predicate that throws (also a cancellation
the client did not cause) counts as `false`. The middleware buffers the body before it asks the predicate and rewinds it
afterwards, so the endpoint always gets the whole body. The grant log names the basis (`endpoint marker` or
`request predicate`). The predicate is asked before the hierarchy lookup on purpose: an administration request on a tenant
that is not a child gets the combined "not an existing child" message, like the REST rule.

The predicate must fail closed: `true` only when **every** operation of the request is an administration operation,
`false` for anything it cannot classify (batches, fragments, unknown fields, unreadable bodies). The operations behind it
check the caller again: the CK method rule (`CkParentTenantAdministrationMethods`, `docs/method-pipeline.md`) and, in the
asset repository, the operation reader. The implementation of the asset repository (`GraphQlParentTenantAdministrationRequest`)
accepts one `application/json` POST made exclusively of the closed method calls or `runtime.operations` reads. Tests:
`tests/Infrastructure.Tests/Middleware/TenantAuthorizationMiddlewareRequestPredicateTests.cs`.
