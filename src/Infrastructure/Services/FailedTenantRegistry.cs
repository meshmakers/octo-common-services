using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>One tenant whose deferred start failed.</summary>
/// <param name="TenantId">Tenant id.</param>
/// <param name="RetryCount">Number of failed retries so far.</param>
/// <param name="FirstFailedAt">When the tenant was first registered as failed.</param>
/// <param name="NextAttemptAt">Earliest time the next retry may run.</param>
public sealed record FailedTenantEntry(
    string TenantId,
    int RetryCount,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset NextAttemptAt);

/// <summary>
///     Thread-safe registry tracking tenant IDs that failed during deferred startup.
///     Used by the background retry service to periodically re-attempt startup for these tenants.
///     Retries back off exponentially (<see cref="TenantSetupRetryOptions" />) and never stop (AB#7249):
///     the former hard stop after ten attempts (~5 minutes) left a tenant "not enabled" for good when the
///     outage — e.g. a node-pool upgrade — outlasted it.
/// </summary>
public class FailedTenantRegistry
{
    private readonly ConcurrentDictionary<string, FailedTenantEntry> _failedTenants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _exhaustionReported = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<TenantSetupRetryOptions>? _options;

    /// <summary>Creates the registry.</summary>
    public FailedTenantRegistry(IOptions<TenantSetupRetryOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private TenantSetupRetryOptions Options => _options?.Value ?? TenantSetupRetryTracker.Default.Options;

    public void Add(string tenantId)
    {
        var now = _timeProvider.GetUtcNow();
        _failedTenants.TryAdd(tenantId, new FailedTenantEntry(tenantId, 0, now, now + Options.DelayAfter(1)));
    }

    public bool Remove(string tenantId)
    {
        _exhaustionReported.TryRemove(tenantId, out _);
        return _failedTenants.TryRemove(tenantId, out _);
    }

    public int IncrementRetryCount(string tenantId)
    {
        var now = _timeProvider.GetUtcNow();
        var options = Options;
        return _failedTenants.AddOrUpdate(
            tenantId,
            _ => new FailedTenantEntry(tenantId, 1, now, now + options.DelayAfter(2)),
            (_, e) => e with { RetryCount = e.RetryCount + 1, NextAttemptAt = now + options.DelayAfter(e.RetryCount + 2) })
            .RetryCount;
    }

    /// <summary>All registered tenants with their retry count (due or not).</summary>
    public IReadOnlyList<KeyValuePair<string, int>> GetAll() =>
        _failedTenants.Values.Select(e => KeyValuePair.Create(e.TenantId, e.RetryCount)).ToArray();

    /// <summary>Tenants whose backoff has elapsed.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> GetDue()
    {
        var now = _timeProvider.GetUtcNow();
        return _failedTenants.Values.Where(e => e.NextAttemptAt <= now)
            .Select(e => KeyValuePair.Create(e.TenantId, e.RetryCount)).ToArray();
    }

    /// <summary>All entries, for health and metrics.</summary>
    public IReadOnlyList<FailedTenantEntry> Snapshot() => _failedTenants.Values.ToArray();

    /// <summary>
    ///     True exactly once per registration: the caller reports "retry budget used up" (log, lifecycle record)
    ///     a single time while the retry itself carries on.
    /// </summary>
    public bool TryMarkExhaustionReported(string tenantId) => _exhaustionReported.TryAdd(tenantId, true);

    public bool HasFailedTenants => !_failedTenants.IsEmpty;
}
