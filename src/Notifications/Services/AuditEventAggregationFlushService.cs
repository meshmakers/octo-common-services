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
/// The flush is bounded by the host's shutdown token: when the event store is too slow, the host stops on time and
/// the summaries not yet written are logged as lost (the counter <c>octo.datapermissions.audit.violations</c> still
/// has every occurrence).
/// </remarks>
internal sealed class AuditEventAggregationFlushService(
    IServiceProvider serviceProvider,
    ILogger<AuditEventAggregationFlushService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var aggregator = serviceProvider.GetService<AggregatingAuditEventSink>();
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
    }
}
