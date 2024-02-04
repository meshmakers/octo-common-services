using System.Collections.Concurrent;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Meshmakers.Octo.Services.Common.Cors;

/// <summary>
///     Implements a CORS policy provider that allows all known clients stored in Octo database
/// </summary>
public class CorsPolicyProvider : ICorsPolicyProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly ConcurrentDictionary<string, CorsPolicy> _corsPolicyDictionary;

    /// <summary>
    ///     Constructor
    /// </summary>
    public CorsPolicyProvider()
    {
        _corsPolicyDictionary = new ConcurrentDictionary<string, CorsPolicy>();
    }

    /// <inheritdoc />
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var tenantId = context.GetTenantId() ?? "";

        if (!_corsPolicyDictionary.TryGetValue(tenantId, out var corsPolicy))
        {
            var knownOriginsProvider = context.RequestServices.GetRequiredService<IKnownOriginsProvider>();
            var origins = await knownOriginsProvider.GetKnownOriginsAsync();

            Logger.Info($"Creating CORS policy from cache: {string.Join(", ", origins)}");

            var policyBuilder = new CorsPolicyBuilder()
                .WithOrigins(origins.ToArray())
                .AllowAnyHeader()
                .AllowAnyMethod();
            corsPolicy = policyBuilder.Build();
            _corsPolicyDictionary.TryAdd(tenantId, corsPolicy);
        }

        return corsPolicy;
    }

    /// <summary>
    ///     Invalidates the cached CORS policy for the given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    public Task InvalidateData(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Task.CompletedTask;
        }

        _corsPolicyDictionary.TryRemove(tenantId, out _);
        return Task.CompletedTask;
    }
}