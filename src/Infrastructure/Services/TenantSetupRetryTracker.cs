using System.Collections.Concurrent;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>One tenant whose setup is currently failing and waiting for its next retry.</summary>
/// <param name="TenantId">Normalized tenant id.</param>
/// <param name="Attempts">Number of failed attempts so far (1 after the first failure).</param>
/// <param name="FirstFailedAt">When the tenant first failed (not reset by further failures).</param>
/// <param name="LastFailedAt">When the latest attempt failed.</param>
/// <param name="NextAttemptAt">Earliest time the next retry may run.</param>
/// <param name="LastError">Message of the latest failure, for logs only.</param>
public sealed record TenantSetupRetryEntry(
    string TenantId,
    int Attempts,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset LastFailedAt,
    DateTimeOffset NextAttemptAt,
    string LastError);

/// <summary>
///     Process-wide, in-memory queue of tenants whose <c>SetupAsync</c> failed (AB#7249). It exists so that the
///     "this tenant will be retried in the background" promise holds <b>without</b> depending on MongoDB:
///     the durable <c>ITenantSetupRetryStore</c> is optional, wired by only some services, and its write can
///     itself time out during the very primary election that made the setup fail.
/// </summary>
/// <remarks>
///     Entries are retried with exponential backoff (<see cref="TenantSetupRetryOptions" />) and never given
///     up on; they leave the queue when a setup succeeds, or when the tenant no longer exists. The state is
///     per process (a restart re-runs the startup loop anyway) and thread safe.
/// </remarks>
public sealed class TenantSetupRetryTracker
{
    private readonly ConcurrentDictionary<string, TenantSetupRetryEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private volatile TenantSetupRetryOptions _options;

    /// <summary>
    ///     The instance every creator service falls back to when none is injected, so a service benefits from
    ///     the retry without any wiring. The DI container hands out this very instance.
    /// </summary>
    public static TenantSetupRetryTracker Default { get; } = new();

    /// <summary>Creates a tracker (tests pass a fake <see cref="TimeProvider" />).</summary>
    public TenantSetupRetryTracker(TimeProvider? timeProvider = null, TenantSetupRetryOptions? options = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new TenantSetupRetryOptions();
    }

    /// <summary>The active options.</summary>
    public TenantSetupRetryOptions Options => _options;

    /// <summary>Replaces the options (called once by DI with the bound configuration).</summary>
    public void Configure(TenantSetupRetryOptions options) => _options = options;

    /// <summary>Number of tenants currently waiting for a retry.</summary>
    public int Count => _entries.Count;

    /// <summary>Records a failed setup and schedules the next attempt with exponential backoff.</summary>
    public TenantSetupRetryEntry RecordFailure(string tenantId, string error)
    {
        var now = _timeProvider.GetUtcNow();
        var options = _options;
        var trimmed = error.Length > 300 ? error[..300] : error;
        return _entries.AddOrUpdate(
            tenantId,
            _ => new TenantSetupRetryEntry(tenantId, 1, now, now, now + options.DelayAfter(1), trimmed),
            (_, existing) =>
            {
                var attempts = existing.Attempts + 1;
                return existing with
                {
                    Attempts = attempts,
                    LastFailedAt = now,
                    NextAttemptAt = now + options.DelayAfter(attempts),
                    LastError = trimmed
                };
            });
    }

    /// <summary>Removes a tenant after a successful setup (or because it is gone); returns the dropped entry.</summary>
    public TenantSetupRetryEntry? Remove(string tenantId)
        => _entries.TryRemove(tenantId, out var entry) ? entry : null;

    /// <summary>Entries whose backoff has elapsed, oldest failure first.</summary>
    public IReadOnlyList<TenantSetupRetryEntry> GetDue()
    {
        var now = _timeProvider.GetUtcNow();
        return _entries.Values.Where(e => e.NextAttemptAt <= now).OrderBy(e => e.FirstFailedAt).ToArray();
    }

    /// <summary>All entries.</summary>
    public IReadOnlyList<TenantSetupRetryEntry> Snapshot() => _entries.Values.OrderBy(e => e.FirstFailedAt).ToArray();
}
