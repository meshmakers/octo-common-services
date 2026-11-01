using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Cors;

/// <summary>
///     Implements a CORS policy provider that allows all known clients stored in Octo database.
///     CORS origins are resolved per tenant from the Identity Clients.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class CorsPolicyProvider : ICorsPolicyProvider
{
    private readonly ILogger<CorsPolicyProvider> _logger;
    private readonly IOptions<OctoSystemConfiguration> _octoSystemConfiguration;
    private readonly IOptions<OctoCorsOptions> _corsOptions;
    private readonly ConcurrentDictionary<string, CorsPolicy> _corsPolicyDictionary;

    /// <summary>
    ///     Constructor
    /// </summary>
    public CorsPolicyProvider(ILogger<CorsPolicyProvider> logger, IOptions<OctoSystemConfiguration> octoSystemConfiguration,
        IOptions<OctoCorsOptions> corsOptions)
    {
        ArgumentNullException.ThrowIfNull(octoSystemConfiguration);
        ArgumentNullException.ThrowIfNull(corsOptions);
        _logger = logger;
        _octoSystemConfiguration = octoSystemConfiguration;
        _corsOptions = corsOptions;

        _corsPolicyDictionary = new ConcurrentDictionary<string, CorsPolicy>();
    }

    /// <inheritdoc />
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var tenantId = context.GetTenantIdFromPath()?.NormalizeString()
                       ?? _octoSystemConfiguration.Value.SystemTenantId.NormalizeString();

        if (_corsPolicyDictionary.TryGetValue(tenantId, out var corsPolicy))
        {
            return corsPolicy;
        }

        var knownOriginsProvider = context.RequestServices.GetRequiredService<IKnownOriginsProvider>();
        var origins = await knownOriginsProvider.GetKnownOriginsAsync(tenantId).ConfigureAwait(false);

        _logger.LogInformation("Creating CORS policy for tenant '{TenantId}': {Origins}", tenantId, string.Join(", ", origins));

        // AB#7270: origins only from the registered clients, never credentials. OctoCorsOptions may add
        // exposed headers and a preflight lifetime; it cannot widen the origin list.
        var policyBuilder = new CorsPolicyBuilder()
            .WithOrigins(origins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod();

        var options = _corsOptions.Value;
        if (options.ExposedHeaders.Count > 0)
        {
            policyBuilder.WithExposedHeaders(options.ExposedHeaders.ToArray());
        }

        if (options.PreflightMaxAge.HasValue)
        {
            policyBuilder.SetPreflightMaxAge(options.PreflightMaxAge.Value);
        }

        corsPolicy = policyBuilder.Build();
        _corsPolicyDictionary.TryAdd(tenantId, corsPolicy);

        return corsPolicy;
    }

    /// <summary>
    ///     Invalidates the cached CORS policy for the given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    public void InvalidateData(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("Attempted to invalidate CORS policy with empty tenant ID");
            return;
        }

        _logger.LogInformation("Invalidating CORS policy for tenant: {TenantId}", tenantId);
        _corsPolicyDictionary.TryRemove(tenantId.NormalizeString(), out _);
    }
}