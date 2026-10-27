using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests.Adversarial;

/// <summary>A signing key with its key id (generated per run, never a real key).</summary>
internal sealed class AdversarialKey : IDisposable
{
    public AdversarialKey(string kid)
    {
        Rsa = RSA.Create(2048);
        Kid = kid;
        Signing = new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256);
    }

    public string Kid { get; }

    public RSA Rsa { get; }

    public SigningCredentials Signing { get; }

    public void Dispose() => Rsa.Dispose();
}

/// <summary>A fake identity service: discovery document and JWKS of one key. No network.</summary>
internal sealed class AdversarialIdentity(AdversarialKey key) : HttpMessageHandler
{
    public const string Authority = "https://identity.adversarial.test/";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        string json;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            json = $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}.well-known/openid-configuration/jwks\"}}";
        }
        else if (path.EndsWith("/jwks", StringComparison.Ordinal))
        {
            var p = key.Rsa.ExportParameters(false);
            json = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + key.Kid + "\",\"n\":\"" +
                   Base64UrlEncoder.Encode(p.Modulus) + "\",\"e\":\"" + Base64UrlEncoder.Encode(p.Exponent) + "\"}]}";
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

/// <summary>Makes tokens shaped like the access tokens of the identity service. Generated, never a real JWT.</summary>
internal static class AdversarialTokens
{
    public const string Tenant = "meshtest";
    public const string Audience = "octo-methods";
    public const string UserA = "65f0c0ffee65f0c0ffee65a1";
    public const string UserB = "65f0c0ffee65f0c0ffee65b2";
    public const string DefaultRole = "Reporting";

    public static string Create(AdversarialKey key, string subject = UserA, string tenant = Tenant,
        string? audience = Audience, bool scope = true, DateTime? expires = null, string issuer = AdversarialIdentity.Authority,
        string[]? roles = null, Action<Dictionary<string, object>>? claims = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["client_id"] = "studio",
            ["tenant_id"] = tenant,
            ["role"] = roles ?? [DefaultRole]
        };
        if (scope)
        {
            payload["scope"] = new[] { "octo_api", "openid" };
        }

        claims?.Invoke(payload);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = payload,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = key.Signing
        });
    }

    /// <summary>An unsigned token (<c>alg: none</c>) that claims to be an administrator of the request tenant.</summary>
    public static string Unsigned()
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $"{{\"iss\":\"{AdversarialIdentity.Authority}\",\"aud\":\"{Audience}\",\"sub\":\"{UserB}\",\"tenant_id\":\"{Tenant}\",\"role\":\"AdminPanelManagement\",\"scope\":\"octo_api\",\"exp\":{exp}}}");
        return header + "." + payload + ".";
    }

    /// <summary>Key confusion: an HS256 token whose secret is the public key material of the real key.</summary>
    public static string SymmetricWithPublicKey(AdversarialKey key)
    {
        var secret = new SymmetricSecurityKey(key.Rsa.ExportRSAPublicKey()) { KeyId = key.Kid };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AdversarialIdentity.Authority,
            Audience = Audience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = UserB, ["tenant_id"] = Tenant, ["scope"] = "octo_api", ["role"] = "AdminPanelManagement"
            },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256)
        });
    }
}
