using Meshmakers.Octo.Services.Methods.Authorization;
using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization.Token;

/// <summary>A signing key with its key id; the JWKS publishes the public part.</summary>
internal sealed class TestKey : IDisposable
{
    public TestKey(string kid)
    {
        Rsa = RSA.Create(2048);
        Kid = kid;
        Signing = new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256);
    }

    public string Kid { get; }

    public RSA Rsa { get; }

    public SigningCredentials Signing { get; }

    public JsonWebKey PublicJwk()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(Rsa.ExportParameters(false)) { KeyId = Kid });
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        return jwk;
    }

    public void Dispose() => Rsa.Dispose();
}

/// <summary>A fake identity service: serves the discovery document and the JWKS, counts the requests.</summary>
internal sealed class FakeIdentityHandler : HttpMessageHandler
{
    public const string Authority = "https://identity.test/";

    private readonly object _gate = new();
    private IReadOnlyList<TestKey> _keys;

    public FakeIdentityHandler(params TestKey[] keys)
    {
        _keys = keys;
    }

    public int Requests { get; private set; }

    public bool Unavailable { get; set; }

    public void Publish(params TestKey[] keys)
    {
        lock (_gate)
        {
            _keys = keys;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests++;
            if (Unavailable)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            }

            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                json = $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}.well-known/openid-configuration/jwks\"}}";
            }
            else if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                var keys = string.Join(",", _keys.Select(k => System.Text.Json.JsonSerializer.Serialize(new
                {
                    kty = "RSA", use = "sig", alg = "RS256", kid = k.Kid,
                    n = Base64UrlEncoder.Encode(k.Rsa.ExportParameters(false).Modulus),
                    e = Base64UrlEncoder.Encode(k.Rsa.ExportParameters(false).Exponent)
                })));
                json = $"{{\"keys\":[{keys}]}}";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Makes tokens shaped like the access tokens of the identity service.</summary>
internal static class TokenFactory
{
    public const string Tenant = "meshtest";
    public const string UserSubject = "6501f2a3b4c5d6e7f8a9b0c1";
    public const string UserClient = "studio";
    public const string ServiceClient = "mesh-bot-job";

    public static string Create(TestKey key, Action<Dictionary<string, object>>? claims = null,
        string issuer = FakeIdentityHandler.Authority, DateTime? expires = null, bool user = true,
        string? tenant = Tenant, string? audience = null, bool scope = true)
    {
        var payload = new Dictionary<string, object>();
        if (user)
        {
            payload["sub"] = UserSubject;
            payload["client_id"] = UserClient;
            payload["role"] = new[] { "UserManagement", "Reporting" };
        }
        else
        {
            payload["client_id"] = ServiceClient;
        }

        if (tenant != null)
        {
            payload["tenant_id"] = tenant;
        }

        if (scope)
        {
            payload["scope"] = new[] { "octo_api", "openid" };
        }

        claims?.Invoke(payload);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = payload,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = key.Signing
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>An unsigned token (<c>alg: none</c>) with the claims of a valid user token.</summary>
    public static string Unsigned(string tenant = Tenant)
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $"{{\"iss\":\"{FakeIdentityHandler.Authority}\",\"sub\":\"{UserSubject}\",\"tenant_id\":\"{tenant}\",\"scope\":\"octo_api\",\"exp\":{exp}}}");
        return header + "." + payload + ".";
    }

    /// <summary>The classic key confusion: an HS256 token whose secret is the public key material.</summary>
    public static string SymmetricWithPublicKey(TestKey key)
    {
        var secret = new SymmetricSecurityKey(key.Rsa.ExportRSAPublicKey()) { KeyId = key.Kid };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = FakeIdentityHandler.Authority,
            Claims = new Dictionary<string, object>
                { ["sub"] = UserSubject, ["tenant_id"] = Tenant, ["scope"] = "octo_api" },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

/// <summary>The validator under test with a fake identity service and a log collector.</summary>
internal sealed class ValidatorFixture : IDisposable
{
    public ValidatorFixture(CkMethodTokenOptions? options = null, TenantAuthorizationOptions? tenantOptions = null)
    {
        Key = new TestKey("key-1");
        Identity = new FakeIdentityHandler(Key);
        Options = options ?? new CkMethodTokenOptions();
        Options.Authority ??= FakeIdentityHandler.Authority;
        Logs = new FakeLogCollector();
        Validator = new CkMethodCallerTokenValidator(Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(tenantOptions ?? new TenantAuthorizationOptions()),
            new FakeHttpClientFactory(Identity), new FakeLogger<CkMethodCallerTokenValidator>(Logs), Time);
    }

    /// <summary>Drives the key cache only; the lifetime check of the tokens uses the system clock.</summary>
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public TestKey Key { get; }

    public FakeIdentityHandler Identity { get; }

    public CkMethodTokenOptions Options { get; }

    public FakeLogCollector Logs { get; }

    public CkMethodCallerTokenValidator Validator { get; }

    public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));

    public ValueTask<CkMethodTokenValidationResult> Validate(string? token, string tenant = TokenFactory.Tenant) =>
        Validator.ValidateAsync(token, tenant, TestContext.Current.CancellationToken);

    public void Dispose() => Key.Dispose();
}
