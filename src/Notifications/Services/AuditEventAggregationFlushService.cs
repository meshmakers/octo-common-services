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
/// <para>
/// A stop after the container is disposed (AB#8872: a <c>WebApplicationFactory</c> disposed twice or by a derived
/// factory stops its hosted services again) is a no-op: disposing the container already disposed the aggregator,
/// and its <see cref="AggregatingAuditEventSink.DisposeAsync"/> published the pending summaries.
/// </para>
/// </remarks>
internal sealed class AuditEventAggregationFlushService(
    IServiceProvider serviceProvider,
    ILogger<AuditEventAggregationFlushService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        AggregatingAuditEventSink? aggregator;
        try
        {
            aggregator = serviceProvider.GetService<AggregatingAuditEventSink>();
        }
        catch (ObjectDisposedException)
        {
            // AB#8872: the container is already disposed, and with it the aggregator, which flushed on dispose.
            _logger.LogDebug("Service provider already disposed; aggregated audit event summaries were flushed on dispose");
            return;
        }

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
