---
description: Shared, persisted and encrypted ASP.NET Data Protection key ring per service (AddOctoDataProtectionKeyRing), its store, isolation and the no-key-ring rule.
applies_to: src/Infrastructure/DataProtection/**, tests/Infrastructure.Tests/DataProtection/**
---

# Shared Data Protection key ring

ASP.NET Data Protection encrypts authentication cookies, the OpenID Connect `state` and `nonce` cookies and
antiforgery tokens. By default every process keeps its own key ring in a local directory
(`~/.aspnet/DataProtection-Keys`, inside the container), so:

- with two or more replicas behind a load balancer, a cookie written by one replica cannot be read by another
  (`Unable to unprotect the message.State` at `/signin-oidc`, `The antiforgery token could not be decrypted`);
- every restart or rollout creates new keys, so running sign-ins and signed-in sessions are lost;
- ASP.NET logs `Storing keys in a directory ... that may not be persisted outside of the container`.

`AddOctoDataProtectionKeyRing(applicationName)` (namespace `Meshmakers.Octo.Services.Infrastructure.DataProtection`)
fixes this for every service that hosts the runtime engine.

```csharp
builder.Services.AddRuntimeEngine();
builder.Services.AddOctoDataProtectionKeyRing("OctoAssetRepositoryServices");
```

## Behaviour

| Topic | Rule |
|---|---|
| Store | `System.Identity/DataProtectionKey` entities in the **system tenant** (the collection the identity service keeps its own key ring in), written through the admin repository of `ISystemContext`. No new collection, database user or secret. |
| Isolation | One key ring per application name. Stored name `<applicationName>/<friendlyName>` (e.g. `OctoAssetRepositoryServices/key-<guid>`); a ring reads only names that start with its own `<applicationName>/`. `SetApplicationName(applicationName)` also isolates the payloads cryptographically. The identity key ring has no prefix (`key-<guid>`), and identity reads only names without `/`. Application names contain no `/` and no whitespace, so `Octo/` never matches `OctoX/…`. |
| Encryption at rest | The secret part of every key (`masterKey`) is wrapped with the platform key ring `SecretEncryption` (AES-256-GCM through `ISecretAttributeProtector`, envelope `enc:v2:<kid>:…`), in the same envelope element the identity service uses. `decryptorType` is version-free (`DataProtectionKeyXml.DecryptorTypeName`); never rename or move `PlatformKeyRingXmlDecryptor`. |
| No key ring configured | The shared store is **not used**: keys are never written unencrypted into it. The service keeps the ASP.NET default (local directory, one ring per process) and logs a warning naming `OCTO_SECRETENCRYPTION__KEYS__<id>` / `OCTO_SECRETENCRYPTION__ACTIVEKEYID`. The repository itself also refuses a write without a key ring. |
| Key ring rotation | Any key id of the platform key ring decrypts, so a rotation with the old key kept leaves every stored key readable. A stored key whose platform key id is gone (or whose key material differs, e.g. a database restored from another installation) is unusable; ASP.NET ignores it and creates a new key (users sign in again). |
| Fresh installation | Nothing is written before the system tenant exists. The key ring preload at host start may fail then; the key is created on first real use after the bootstrap. |
| Concurrency | Elements are inserted once per name (unique index); a replica that loses the race verifies the element is stored and goes on. |

## Requirements

- `AddRuntimeEngine()` (provides `ISystemContext` and `ISecretAttributeProtector`) and the system database admin
  credentials (`OCTO_SYSTEM__ADMINUSERPASSWORD`), which every engine host receives from the chart.
- The `SecretEncryption` key ring (`OCTO_SECRETENCRYPTION__*`), delivered by the chart to every engine host.
- The `System.Identity` construction kit in the system tenant (installed by the identity service).
- Deploy an identity service that ignores prefixed names before the first service using this helper. An older identity
  service tolerates encrypted service keys (it cannot resolve their decryptor and ignores them; it may create one extra
  key of its own) but would rewrite an unencrypted one, which this helper never writes.

## Which services call it

Services that use cookies, OpenID Connect sign-in or antiforgery: asset repository, bot, reporting and AI services.
Pure JWT bearer APIs (no cookie, no antiforgery, no `IDataProtector`) do not need it; they still log the ASP.NET
directory warning at start, which is harmless there.

## Tests

`tests/Infrastructure.Tests/DataProtection/OctoDataProtectionKeyRingTests.cs`: two replicas of a service unprotect
each other's payloads, keys survive a restart, different application names cannot unprotect each other's payloads and
keep separate keys, prefix collisions (identity names, common application name prefixes), new keys always encrypted
with a key ring, no write to the shared store without a key ring, nothing stored before the system tenant exists.
