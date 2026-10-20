using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     Default <see cref="ICkMethodCallerTokenValidator" />: validates issuer, signature (keys from the identity JWKS,
///     cached and refreshed on key rotation), expiry with a clock skew and, when configured, the audience; then applies the
///     scope and tenant rules of the HTTP API. See <see cref="CkMethodTokenOptions" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tenant rule</b> (the one of <c>TenantAuthorizationMiddleware</c>, always enforcing): the <c>tenant_id</c> claim
///         must equal the tenant of the request. <c>allowed_tenants</c> is a tenant-selection hint, never an authorization.
///         A token without <c>sub</c> is a service token (client credentials): it needs <c>client_id</c>, and is
///         accepted for the request tenant or when the client is listed in
///         <c>TenantAuthorization:CrossTenantServiceClientIds</c>. The <c>LogOnly</c> and <c>Disabled</c> modes of the HTTP
///         side are never applied, and there is no parent-tenant rule.
///     </para>
///     <para>
///         <b>Sensitive data.</b> The token and its claims never reach a log line, an exception message or the failure: the
///         log names the tenant, a fixed reason and an internal category, nothing taken from the token.
///     </para>
/// </remarks>
internal sealed class CkMethodCallerTokenValidator : ICkMethodCallerTokenValidator
{
    private const string TenantIdClaimType = "tenant_id";
    private const string ClientIdClaimType = "client_id";
    private const string SubClaimType = "sub";

    // A JWT of the identity service is a few hundred bytes to a few KB; refuse absurd input before parsing it.
    private const int MaxTokenLength = 32 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CkMethodCallerTokenValidator> _logger;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private OpenIdConnectConfiguration? _keys;
    private DateTimeOffset _keysFetchedAt;
    private DateTimeOffset _nextScheduledRefresh;
    private readonly CkMethodTokenOptions _options;
    private readonly TenantAuthorizationOptions _tenantOptions;
    private readonly JsonWebTokenHandler _handler = new();

    public CkMethodCallerTokenValidator(
        IOptions<CkMethodTokenOptions> options,
        IOptions<TenantAuthorizationOptions> tenantOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<CkMethodCallerTokenValidator> logger,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _tenantOptions = tenantOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async ValueTask<CkMethodTokenValidationResult> ValidateAsync(string? accessToken, string expectedTenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTenantId);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Refuse(expectedTenantId, CkMethodTokenReasons.TokenMissing, Category.NoToken);
        }

        if (accessToken.Length > MaxTokenLength)
        {
            return Refuse(expectedTenantId, CkMethodTokenReasons.TokenInvalid, Category.TooLong);
        }

        ClaimsPrincipal principal;
        string? lateReason = null;
        if (_options.DisableValidation)
        {
            if (ReadUnverified(accessToken) is not { } unverified)
            {
                return Refuse(expectedTenantId, CkMethodTokenReasons.TokenInvalid, Category.Malformed);
            }

            principal = unverified;
        }
        else
        {
            var first = await ValidateTokenAsync(accessToken, lifetime: true, audience: true, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsValid)
            {
                principal = new ClaimsPrincipal(first.ClaimsIdentity);
            }
            else
            {
                lateReason = first.Exception switch
                {
                    SecurityTokenExpiredException => CkMethodTokenReasons.TokenExpired,
                    SecurityTokenInvalidAudienceException => CkMethodTokenReasons.AudienceMismatch,
                    _ => null
                };
                if (lateReason is null)
                {
                    return Refuse(expectedTenantId, CkMethodTokenReasons.TokenInvalid, Category.Rejected);
                }

                // "Expired" or "wrong audience" is only worth reporting for a token that is genuine otherwise: validate again
                // without those two checks, so a forged token never learns which of its flaws was the first one found.
                var second = await ValidateTokenAsync(accessToken, lifetime: false, audience: false, cancellationToken)
                    .ConfigureAwait(false);
                if (!second.IsValid)
                {
                    return Refuse(expectedTenantId, CkMethodTokenReasons.TokenInvalid, Category.Rejected);
                }

                principal = new ClaimsPrincipal(second.ClaimsIdentity);
            }
        }

        var claimsFailure = CheckClaims(principal, expectedTenantId, out var category);
        if (claimsFailure is not null)
        {
            return Refuse(expectedTenantId, claimsFailure, category);
        }

        if (lateReason is not null)
        {
            return Refuse(expectedTenantId, lateReason,
                lateReason == CkMethodTokenReasons.TokenExpired ? Category.Expired : Category.Audience);
        }

        return CkMethodTokenValidationResult.Valid(BuildPrincipal(principal, expectedTenantId));
    }

    private string? CheckClaims(ClaimsPrincipal principal, string expectedTenantId, out Category category)
    {
        category = Category.None;

        if (!string.IsNullOrWhiteSpace(_options.RequiredScope) && !principal
                .FindAll(Infrastructure.InfrastructureCommon.ClaimScope)
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Contains(_options.RequiredScope, StringComparer.Ordinal))
        {
            category = Category.ScopeMissing;
            return CkMethodTokenReasons.TokenInvalid;
        }

        var tokenTenant = principal.FindFirstValue(TenantIdClaimType);
        var tenantMatches = !string.IsNullOrEmpty(tokenTenant) &&
                            string.Equals(tokenTenant, expectedTenantId, StringComparison.OrdinalIgnoreCase);
        var hasSubject = !string.IsNullOrWhiteSpace(principal.FindFirstValue(SubClaimType));
        if (hasSubject)
        {
            if (tenantMatches)
            {
                return null;
            }

            category = Category.UserTenant;
            return CkMethodTokenReasons.TenantNotAllowed;
        }

        // no sub: a service token (client credentials) is identified by its client_id
        var clientId = principal.FindFirstValue(ClientIdClaimType);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            category = Category.NoIdentity;
            return CkMethodTokenReasons.TokenInvalid;
        }

        if (tenantMatches || _tenantOptions.IsCrossTenantServiceClient(clientId))
        {
            return null;
        }

        category = Category.ServiceTenant;
        return CkMethodTokenReasons.TenantNotAllowed;
    }

    private static CkMethodPrincipal BuildPrincipal(ClaimsPrincipal principal, string expectedTenantId)
    {
        var mapped = CkMethodPrincipal.FromClaimsPrincipal(principal);
        var sub = principal.FindFirstValue(SubClaimType);
        var clientId = principal.FindFirstValue(ClientIdClaimType);
        return mapped with
        {
            SubjectId = string.IsNullOrWhiteSpace(sub) ? null : sub,
            ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            TenantId = expectedTenantId
        };
    }

    private async Task<TokenValidationResult> ValidateTokenAsync(string accessToken, bool lifetime, bool audience,
        CancellationToken cancellationToken)
    {
        var config = await GetKeysAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(config, accessToken, lifetime, audience, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rotation: the identity service signed with a key we have not seen. One forced refresh (throttled by
            // KeyRefreshInterval), then a second try with the new keys.
            var refreshed = await GetKeysAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(refreshed, config))
            {
                result = await RunAsync(refreshed, accessToken, lifetime, audience, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return result;
    }

    private async Task<TokenValidationResult> RunAsync(OpenIdConnectConfiguration config, string accessToken,
        bool lifetime, bool audience, CancellationToken cancellationToken)
    {
        var parameters = CreateParameters(config, lifetime, audience);
        try
        {
            return await _handler.ValidateTokenAsync(accessToken, parameters).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Never rethrow or log e: the message of a validation exception can quote parts of the token. Only the type goes
            // into the debug log.
            _logger.LogDebug("Caller token validation threw {ExceptionType}", e.GetType().Name);
            return new TokenValidationResult { IsValid = false, Exception = new SecurityTokenException("rejected") };
        }
    }

    private TokenValidationParameters CreateParameters(OpenIdConnectConfiguration config, bool lifetime, bool audience)
    {
        var validateAudience = audience && _options.ValidAudiences.Count > 0;
        IEnumerable<string> algorithms = _options.ValidAlgorithms.Count > 0 ? _options.ValidAlgorithms : CkMethodTokenOptions.DefaultAlgorithms;
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = GetIssuer(),
            ValidateAudience = validateAudience,
            ValidAudiences = validateAudience ? _options.ValidAudiences.ToArray() : null,
            ValidateLifetime = lifetime,
            RequireExpirationTime = true,
            ClockSkew = _options.ClockSkew,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = algorithms.Where(a => !a.StartsWith("HS", StringComparison.OrdinalIgnoreCase) &&
                                                    !string.Equals(a, "none", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            ValidTypes = _options.ValidTypes.Count > 0 ? _options.ValidTypes.ToArray() : null,
            // the claim names stay as in the token (sub, role, scope, ...); no inbound mapping
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    }

    /// <summary>
    ///     The signing keys, cached. A scheduled refresh (<see cref="CkMethodTokenOptions.KeyAutomaticRefreshInterval" />) keeps
    ///     serving the last keys when the identity service cannot be reached; a forced refresh (unknown key id) is throttled
    ///     by <see cref="CkMethodTokenOptions.KeyRefreshInterval" /> and counted from the last download.
    /// </summary>
    private async Task<OpenIdConnectConfiguration> GetKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var current = _keys;
        if (current is not null && !forceRefresh && now < _nextScheduledRefresh)
        {
            return current;
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            current = _keys;
            if (current is not null)
            {
                if (forceRefresh ? now - _keysFetchedAt < _options.KeyRefreshInterval : now < _nextScheduledRefresh)
                {
                    return current; // somebody else refreshed meanwhile, or the throttle holds
                }
            }

            var address = GetMetadataAddress() ?? throw new CkMethodTokenKeysUnavailableException();
            try
            {
                var retriever = new HttpDocumentRetriever(_httpClientFactory.CreateClient(CkMethodTokenOptions.HttpClientName))
                {
                    RequireHttps = _options.RequireHttpsMetadata
                };
                var loaded = await OpenIdConnectConfigurationRetriever
                    .GetAsync(address, retriever, cancellationToken).ConfigureAwait(false);
                if (loaded.SigningKeys.Count == 0)
                {
                    throw new InvalidOperationException("The issuer publishes no signing key.");
                }

                _keys = loaded;
                _keysFetchedAt = now;
                _nextScheduledRefresh = now + _options.KeyAutomaticRefreshInterval;
                return loaded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // only the exception type is logged: the message may hold the address of the identity service
                _logger.LogError(
                    "The signing keys of the caller token issuer cannot be loaded ({ExceptionType}){Fallback}",
                    e.GetType().Name, current is null ? "; caller tokens are refused until they can" : "; the cached keys stay in use");
                if (current is null)
                {
                    throw new CkMethodTokenKeysUnavailableException();
                }

                // do not retry on every call while the identity service is down
                _keysFetchedAt = now;
                _nextScheduledRefresh = now + _options.KeyRefreshInterval;
                return current;
            }
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private string? GetMetadataAddress()
    {
        if (!string.IsNullOrWhiteSpace(_options.MetadataAddress))
        {
            return _options.MetadataAddress;
        }

        return string.IsNullOrWhiteSpace(_options.Authority)
            ? null
            : EnsureTrailingSlash(_options.Authority) + ".well-known/openid-configuration";
    }

    private string GetIssuer() => !string.IsNullOrWhiteSpace(_options.ValidIssuer)
        ? _options.ValidIssuer
        : EnsureTrailingSlash(_options.Authority!);

    private ClaimsPrincipal? ReadUnverified(string accessToken)
    {
        try
        {
            var token = new JsonWebToken(accessToken);
            return new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "Unverified"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private CkMethodTokenValidationResult Refuse(string tenantId, string reason, Category category)
    {
        // Fixed vocabulary only: the tenant is the one of the request, reason and category are constants. Nothing is
        // taken from the token.
        _logger.LogInformation(
            "Caller token refused for tenant {TenantId}: {Reason} ({Category})", tenantId, reason, category);
        return CkMethodTokenValidationResult.Refused(reason);
    }

    private enum Category
    {
        None,
        NoToken,
        TooLong,
        Malformed,
        Rejected,
        Expired,
        Audience,
        ScopeMissing,
        UserTenant,
        ServiceTenant,
        NoIdentity
    }
}

/// <summary>
///     The signing keys of the token issuer cannot be loaded (identity service unreachable, no authority configured).
///     The caller token cannot be judged, so the call is not executed; the handler host answers
///     <c>METHOD_HANDLER_FAILED</c> (not <c>METHOD_FORBIDDEN</c>: the caller did nothing wrong).
/// </summary>
public sealed class CkMethodTokenKeysUnavailableException()
    : InvalidOperationException("The signing keys of the caller token issuer are not available.");
