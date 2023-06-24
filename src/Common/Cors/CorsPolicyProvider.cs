using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;
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
    private readonly IOctoClientStore _clientStore;
    private CorsPolicy? _corsPolicy;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="clientStore">Client store object to access all available clients.</param>
    /// <param name="distributedCache">Instance of distributed cache</param>
    public CorsPolicyProvider(IOctoClientStore clientStore, IDistributedWithPubSubCache distributedCache)
    {
        _clientStore = clientStore;
        _channel = distributedCache.Subscribe<string>(CacheCommon.KeyCorsClients);
        _channel.OnMessage(OnInvalidateData);
    }

    /// <inheritdoc />
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (_corsPolicy == null)
        {
            var clients = await _clientStore.GetClients();
            var origins = clients.SelectMany(x => x.AllowedCorsOrigins)
                .ToArray();

            Logger.Info($"Creating CORS policy from cache: {string.Join(", ", origins)}");

            var policyBuilder = new CorsPolicyBuilder()
                .WithOrigins(origins)
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
