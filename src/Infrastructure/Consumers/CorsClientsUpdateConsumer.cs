using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Consumers;

/// <summary>
///     Consumer for <see cref="CorsClientsUpdate" /> messages.
/// </summary>
public class CorsClientsUpdateConsumer : IDistributedConsumer<CorsClientsUpdate>
{
    private readonly CorsPolicyProvider _corsPolicyProvider;
    private readonly ILogger<CorsClientsUpdateConsumer> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="corsPolicyProvider"></param>
    public CorsClientsUpdateConsumer(ILogger<CorsClientsUpdateConsumer> logger, CorsPolicyProvider corsPolicyProvider)
    {
        _logger = logger;
        _corsPolicyProvider = corsPolicyProvider;
    }

    /// <inheritdoc />
    public Task ConsumeAsync(IDistributedContext<CorsClientsUpdate> context)
    {
        _logger.LogInformation("Cors client update for tenant received: {Text}", context.Message.TenantId);

        var key = context.Message.TenantId?.NormalizeString();

        _corsPolicyProvider.InvalidateData(key);

        return Task.CompletedTask;
    }
}