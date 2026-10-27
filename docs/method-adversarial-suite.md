# CK method adversarial suite (AB#6915, F3.4b)

A repeatable attack suite over the method path: HTTP token layer, gateway, authorization filter, bus, remote handler host,
logs and traces. It gives F3.4 / Phase 3 an exit on evidence and lets every later change to the method path rerun the attacks.

## Where it lives and how to rerun it

| Layer | Repository | Tests | Command |
|-------|------------|-------|---------|
| Bus, remote handler host with token validation, gateway pipeline (bus and in-process), canary scan | octo-common-services | `tests/Methods.IntegrationTests/Adversarial/` (`CkMethodAdversarialBusTests`, trait `Category=Adversarial`) | `dotnet test tests/Methods.IntegrationTests -c DebugL --filter "Category=Adversarial"` |
| Real JWT bearer configuration of the service + tenant gate + method authorization (HTTP) | octo-asset-repo-services | `tests/AssetRepositoryServices.UnitTests/Adversarial/BearerTokenAttackTests.cs` | `dotnet test tests/AssetRepositoryServices.UnitTests -c DebugL --filter "FullyQualifiedName~Adversarial"` |
| Gateway (authorize first, validator, logs) | octo-asset-repo-services | `.../Adversarial/CkMethodGatewayAdversarialTests.cs` | same |
| GraphQL mutation input, deadline, no user | octo-asset-repo-services | `tests/AssetRepositoryServices.IntegrationTests/GraphQL/CkV2/MethodAdversarialGraphQlTests.cs` | `dotnet test tests/AssetRepositoryServices.IntegrationTests -c DebugL --filter "FullyQualifiedName~MethodAdversarialGraphQlTests"` |

Everything runs in process or against private Testcontainers (RabbitMQ, MongoDB). No shared stack, no test-2. Tokens are generated
per run with a throw-away RSA key and a fake identity service. Each run writes its table to `adversarial-results.md` next to the
test assembly (`bin/<Configuration>/net10.0/`).

The bus suite has three parties on one broker: the **handler host** (token validation on, audience configured), the **gateway**
(dispatcher, authorization filter band 200, audit probe band 100; the HTTP principal is in `CkMethodPrincipalScope`) and the
**attacker**, a bus participant without any filter that publishes crafted `CkMethodRequest`s straight to the method queue.

A gap is pinned twice: a `..._Gap_`/`..._Limitation_` test asserts today's behaviour (it turns red when the gap is closed) and a
skipped test asserts the expected behaviour (enable it then). A new transport, filter or handler host adds its cases here.

## Cases and observed results

Observed code is `code / reason`. "none" in the side-effect column means the handler was not called, the target not read and
nothing dispatched.

| Case | Attack | Layer | Observed | Side effect |
|------|--------|-------|----------|-------------|
| C01 | forged Caller DTO (administrator), no token | bus / handler host | METHOD_FORBIDDEN / token-missing | none |
| C01b | forged Caller DTO next to a valid token of a plain user | bus / handler host | runs as the token owner; DTO is informational | by design |
| C01 | unauthenticated principal; forged Caller member in the GraphQL input | gateway, GraphQL | METHOD_FORBIDDEN / not-authorized; input member rejected by validation | none |
| C02 | token of another tenant, parent tenant, token replayed for another tenant | bus; HTTP (gate Enforce) | METHOD_FORBIDDEN / tenant-not-allowed; HTTP 403 | none |
| C03 | expired token | bus; HTTP | METHOD_FORBIDDEN / token-expired; HTTP 401 | none |
| C04 | wrong or missing audience | bus (audience configured) | METHOD_FORBIDDEN / audience-mismatch | none |
| C05 | wrong signature (foreign key same kid, unknown kid, flipped signature, tampered payload) | bus; HTTP | METHOD_FORBIDDEN / token-invalid; HTTP 401 | none |
| C06 | `alg: none`, HS256 with public key material | bus; HTTP | METHOD_FORBIDDEN / token-invalid; HTTP 401 | none |
| C07 | token without `octo_api` (also with role and admin role; `octo_apiX`) | bus; HTTP; gateway | token-invalid on the bus; HTTP layer passes, method authorization refuses (403); gateway METHOD_FORBIDDEN / not-authorized | none |
| C07b | other issuer, token above 32 KiB, no token | bus; HTTP | token-invalid; HTTP 401 | none |
| C08 | allowSelf with another user's id (also upper case, unparsable equal strings) | gateway pipeline bus + in-process, gateway, GraphQL | METHOD_FORBIDDEN / not-authorized (control: own id in upper case passes) | none |
| C09 | method without `authorization`, called by its owner; forged admin DTO; look-alike roles | gateway pipeline bus + in-process, gateway | METHOD_FORBIDDEN / not-authorized (control: administrator passes) | none |
| C10 | caller without the right: existing vs missing target; default-deny; unknown method | gateway pipeline bus + in-process, gateway | byte-identical METHOD_FORBIDDEN / not-authorized, target never read | none |
| C11 | deadline in the past | bus (valid and missing token), gateway bus + in-process, GraphQL | METHOD_TIMEOUT, same answer for any sender | none |
| C11c-e | deadline `DateTime.MaxValue` | gateway (clamped, runs), bus with forged token (refused), bus with valid token | no overflow; valid calls run, forged ones METHOD_FORBIDDEN | see L-4 |
| C12 | parameter payload far above any limit (8 MiB); malformed, non-object, 5000-deep JSON | host, gateway | **8 MiB accepted and executed (F-2)**; malformed forms METHOD_VALIDATION_FAILED without the value | F-2 |
| C13 | handler answers with a `METHOD_`-prefixed code that is no platform code | handler SDK, bus + in-process | METHOD_HANDLER_FAILED, code never forwarded; **responder code forwarded by the GraphQL error (F-3)** | none |
| C14 | canary (sensitive parameter, token, claim value) over gateway, bus, host and all attacks | logs, audit view, activities, responses | no plaintext found | none |
| C15 | valid message of A replayed with the Caller DTO of administrator B; for another tenant | bus / handler host | runs as A, never as B; other tenant tenant-not-allowed | none |
| C16 | valid token without the declared role published on the bus | bus / handler host | **handler runs (F-1)** | F-1 |

## Findings (not fixed in this story; for Odin / Gerald)

| Id | Finding | Severity | Status |
|----|---------|----------|--------|
| F-1 | The remote handler host authenticates the caller but does not re-check the declared authorization (roles, scopes, allowSelf, default-deny). Declared authorization runs in the calling process only. A bus participant with publish rights and any valid tenant token can reach a role-protected handler. `methodHandlers.md` makes the check a handler duty. Test: `C16_Gap_...` (pin), `C16_RemoteHost_RefusesACallerWithoutTheDeclaredRole` (skipped) | medium (needs bus publish rights and a valid token) | open, decision needed |
| F-2 | No limit for the size of method parameters in gateway, validator, bus or host; no documented code | low to medium (resource use) | open |
| F-3 | The GraphQL error forwards any code a responder writes, including unknown `METHOD_` codes | low (needs a rogue responder on the queue) | open |

## Accepted limitations (need Odin's signature)

| Id | Limitation | Source |
|----|-----------|--------|
| L-1 | An intact message of A replayed within the token lifetime runs again as A (no binding of token to invocation). Test `C15c_Limitation_...` | decision AB#7039 / OD-11 (B0, B1 later) |
| L-2 | The HTTP API does not validate the audience (`ValidateAudience` false platform-wide); a remote host validates it only when `Methods:Token:ValidAudiences` is set. Test `C04_Limitation_...` (asset-repo) | AB#6913 adjustment 1 |
| L-3 | The method gateway does not compare token tenant and route tenant itself; it relies on the tenant gate. asset-repo registers the gate in `LogOnly` as code default until the chart flips it (AB#6728). Remote handlers re-check the tenant; an in-process handler would not. Tests `C02b_`, `C02c_Limitation_...` | AB#6728 |
| L-4 | A message published straight on the queue with a far-future deadline is not clamped by the host (only the dispatcher clamps to the platform maximum); the host cancels after at most ~24 days. Needs a valid token and bus publish rights. Test `C11e_` | open observation |
