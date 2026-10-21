using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Reports <see cref="HealthStatus.Degraded" /> while a tenant has been failing its setup or deferred start
///     for longer than <see cref="TenantSetupRetryOptions.DegradedAfter" /> (AB#7249). Such a tenant is
///     "not enabled" for every adapter and request, so the service must say so itself instead of leaving the
///     symptom (adapters offline) to be diagnosed. Degraded rather than Unhealthy and not tagged
///     <c>ready</c>: a service that serves its healthy tenants must stay in the load balancer.
/// </summary>
public class TenantSetupHealthCheck(
    TenantSetupRetryTracker tracker,
    FailedTenantRegistry registry,
    IOptions<TenantSetupRetryOptions> options,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var threshold = options.Value.DegradedAfter;

        var pending = tracker.Snapshot().Select(e => (e.TenantId, e.FirstFailedAt, Attempts: e.Attempts))
            .Concat(registry.Snapshot().Select(e => (e.TenantId, e.FirstFailedAt, Attempts: e.RetryCount)))
            .ToList();

        if (pending.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("All tenants are set up."));
        }

        IReadOnlyDictionary<string, object> data = pending.GroupBy(p => p.TenantId).ToDictionary<IGrouping<string, (string TenantId, DateTimeOffset FirstFailedAt, int Attempts)>, string, object>(
            g => g.Key,
            g => (object)$"failing for {(now - g.Min(p => p.FirstFailedAt)).TotalSeconds:F0}s, " +
                 $"{g.Max(p => p.Attempts)} attempt(s)");

        var stuck = pending.Where(p => now - p.FirstFailedAt > threshold).Select(p => p.TenantId).Distinct()
            .Order(StringComparer.Ordinal).ToList();

        return Task.FromResult(stuck.Count == 0
            ? HealthCheckResult.Healthy(
                $"{data.Count} tenant setup(s) failing for less than {threshold}; being retried.", data)
            : HealthCheckResult.Degraded(
                $"Setup of tenant(s) {string.Join(", ", stuck)} has been failing for more than {threshold}; " +
                "the tenant is not enabled until a retry succeeds.", data: data));
    }
}
