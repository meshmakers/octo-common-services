using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using NLog;

namespace Meshmakers.Octo.Services.Common.Cors;

/// <summary>
///     Implements a CORS policy provider that allows all known clients stored in Octo database
/// </summary>
public class CorsPolicyProvider : ICorsPolicyProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IChannel<string> _channel;
    private readonly IKnownOriginsProvider _knownOriginsProvider;
    private CorsPolicy? _corsPolicy;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="knownOriginsProvider">Client store object to access all available clients.</param>
    /// <param name="distributedCache">Instance of distributed cache</param>
    public CorsPolicyProvider(IKnownOriginsProvider knownOriginsProvider, IDistributedWithPubSubCache distributedCache)
    {
        _knownOriginsProvider = knownOriginsProvider;
        _channel = distributedCache.Subscribe<string>(CacheCommon.KeyCorsClients);
        _channel.OnMessage(OnInvalidateData);
    }

    /// <inheritdoc />
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (_corsPolicy == null)
        {
            var origins = await _knownOriginsProvider.GetKnownOriginsAsync();

            Logger.Info($"Creating CORS policy from cache: {string.Join(", ", origins)}");

            var policyBuilder = new CorsPolicyBuilder()
                .WithOrigins(origins.ToArray())
                .AllowAnyHeader()
                .AllowAnyMethod();
            _corsPolicy = policyBuilder.Build();
        }

        return _corsPolicy;
    }

    private Task OnInvalidateData(IChannelMessage<string> arg)
    {
        _corsPolicy = null;
        return Task.CompletedTask;
    }
}
