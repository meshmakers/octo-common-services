using Meshmakers.Octo.Runtime.Engine.AuditTrails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
/// Publishes the pending summaries of the engine's <see cref="AggregatingAuditEventSink"/> when the host stops
/// (AB#7279). The aggregator also flushes when the container disposes it, but by then
/// <see cref="EventRepositoryAuditEventSink"/> can no longer open the service scope it writes through, so the
/// summaries of the last window would only reach the error log instead of the tenant event store.
/// </summary>
/// <remarks>
/// <para>
/// The flush is bounded by the host's shutdown token: when the event store is too slow, the host stops on time and
/// the summaries not yet written are logged as lost (the counter <c>octo.datapermissions.audit.violations</c> still
/// has every occurrence).
/// </para>
/// <para>
/// The aggregator is resolved once in <see cref="StartAsync"/>, never in <see cref="StopAsync"/> (AB#8876): a host
/// can stop after its service provider was disposed (<c>WebApplicationFactory.Dispose</c>, shutdown races), and a
/// resolve from the disposed provider threw <see cref="ObjectDisposedException"/> out of <c>Host.StopAsync</c>. The
/// aggregator is not resolved in the constructor, so building the hosted services does not create the sink chain.
/// </para>
/// </remarks>
internal sealed class AuditEventAggregationFlushService(
    IServiceProvider serviceProvider,
    ILogger<AuditEventAggregationFlushService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private AggregatingAuditEventSink? _aggregator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _aggregator = serviceProvider.GetService<AggregatingAuditEventSink>();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var aggregator = _aggregator;
        if (aggregator == null)
        {
            return;
        }

        try
        {
            await aggregator.FlushPendingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Host shutdown timeout reached before all aggregated audit event summaries were stored; the rest is dropped");
        }
        catch (ObjectDisposedException ex)
        {
            // The container was disposed while the host was stopping; what is still pending is published when the
            // container disposes the aggregator.
            _logger.LogDebug(ex,
                "Service provider already disposed when the host stopped; aggregated audit event summaries were not flushed here");
        }
    }
}
