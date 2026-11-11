using Meshmakers.Octo.Runtime.Engine.AuditTrails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
/// Publishes the pending summaries of the engine's <see cref="AggregatingAuditEventSink"/> when the host stops
/// (AB#7279). The aggregator also flushes when the container disposes it, but by then
/// <see cref="EventRepositoryAuditEventSink"/> can no longer open the service scope it writes through, so the
/// summaries of the last window would only reach the error log instead of the tenant event store.
/// </summary>
internal sealed class AuditEventAggregationFlushService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var aggregator = serviceProvider.GetService<AggregatingAuditEventSink>();
        return aggregator?.FlushPendingAsync() ?? Task.CompletedTask;
    }
}
