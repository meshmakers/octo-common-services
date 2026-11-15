using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     The current generation of a tenant id (AB#8999): the <see cref="TenantLifecycleRecord.CreatedUtc" /> of its
///     lifecycle record in the format of <see cref="TenantGenerationFormat" />, cached per service instance.
/// </summary>
public interface ITenantGenerationReader
{
    /// <summary>
    ///     The current generation of <paramref name="tenantId" />, or <c>null</c> when the tenant has none (no lifecycle
    ///     record or no <c>CreatedUtc</c>: a legacy tenant; or a host without the lifecycle store).
    /// </summary>
    /// <param name="tenantId">The tenant id (compared case-insensitively, like the lifecycle store).</param>
    /// <param name="refresh">
    ///     Read again instead of using the cached value, unless the cached value is younger than one second (so a stream
    ///     of requests cannot turn a refresh into one read per request).
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, never the shared read.</param>
    /// <exception cref="TenantGenerationLookupException">The lifecycle record could not be read.</exception>
    ValueTask<string?> GetGenerationAsync(string tenantId, bool refresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the cached value of <paramref name="tenantId" /> (tenant create, update and delete events).</summary>
    void Invalidate(string tenantId);
}

/// <summary>The current generation of a tenant could not be read. Never carries token values.</summary>
public sealed class TenantGenerationLookupException(string tenantId, Exception inner)
    : Exception($"The generation of tenant '{tenantId}' could not be read ({inner.GetType().Name}).", inner);

/// <summary>
///     Default <see cref="ITenantGenerationReader" /> over <see cref="ITenantLifecycleStore" /> (system database).
/// </summary>
/// <remarks>
///     <para>
///         <b>At most one read per tenant per TTL window per instance.</b> The value — also "no generation" — is cached for
///         <see cref="TenantAuthorizationOptions.TenantGenerationCacheDuration" /> (default 30 s). Concurrent misses for one
///         tenant share one read. A failed read is answered as failed for <see cref="FailureBackoff" /> (2 s), then read
///         again, so a MongoDB outage costs one read per tenant per back-off, not one per request.
///     </para>
///     <para>
///         <b>Why the TTL must stay below the delete tombstone time.</b> A deleted tenant keeps its lifecycle record as the
///         <c>Deleting</c> tombstone until the delete has settled (about 90 s, <c>ReconcileDeletingTenantsAsync</c>); only
///         then can the id be created again. With a TTL below that, no cached generation of the deleted tenant survives into
///         the recreated one, even on an instance that missed the tenant events. The tenant create/update/delete events
///         (<c>PosCreateTenant</c>, <c>PosUpdateTenant</c>, <c>PreUpdateTenant</c>, <c>PreDeleteTenant</c>) drop the entry
///         earlier where the host receives them.
///     </para>
///     <para>
///         The cache is capped at 1024 tenants; tenant ids come from validated tokens, so a real estate stays far below.
///     </para>
/// </remarks>
public sealed class TenantGenerationReader : ITenantGenerationReader
{
    private const int MaxCacheEntries = 1024;
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How long a failed read is answered from the cache before the store is asked again: during a MongoDB outage
    ///     this keeps it at one read (and one error log) per tenant per interval instead of one per request.
    /// </summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Upper bound of <see cref="TenantAuthorizationOptions.TenantGenerationCacheDuration" />: it must stay below the
    ///     delete tombstone time (about 90 s). A larger configured value is clamped, with one warning.
    /// </summary>
    public static readonly TimeSpan MaxCacheDuration = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ITenantLifecycleStore? _lifecycleStore;
    private readonly ILogger<TenantGenerationReader> _logger;
    private readonly IOptions<TenantAuthorizationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private int _noStoreLogged;
    private int _clampLogged;

    /// <summary>Creates the reader. Without a lifecycle store every tenant has no generation (not checked).</summary>
    public TenantGenerationReader(IOptions<TenantAuthorizationOptions> options, ILogger<TenantGenerationReader> logger,
        ITenantLifecycleStore? lifecycleStore = null, TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger = logger;
        _lifecycleStore = lifecycleStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetGenerationAsync(string tenantId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_lifecycleStore is null)
        {
            if (Interlocked.Exchange(ref _noStoreLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "No tenant lifecycle store is registered: the tenant generation (tenant_gen) of access tokens is not checked in this service (AB#8999)");
            }

            return null;
        }

        var key = Normalize(tenantId);
        var ttl = EffectiveTtl();
        var now = _timeProvider.GetUtcNow();
        CacheEntry entry;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out entry!) || !IsUsable(entry, now, ttl, refresh))
            {
                entry = new CacheEntry(now);
                entry.Load = LoadAsync(key, entry);
                if (ttl > TimeSpan.Zero)
                {
                    if (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(key))
                    {
                        Trim(now, ttl);
                    }

                    _cache[key] = entry;
                }
            }
        }

        return await entry.Load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Invalidate(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        lock (_gate)
        {
            _cache.Remove(Normalize(tenantId));
        }
    }

    /// <summary>The cache key, the same form as the lifecycle store and identity use (trimmed, lower case).</summary>
    internal static string Normalize(string tenantId) => tenantId.Trim().ToLowerInvariant();

    private TimeSpan EffectiveTtl()
    {
        var ttl = _options.Value.TenantGenerationCacheDuration;
        if (ttl <= MaxCacheDuration)
        {
            return ttl;
        }

        if (Interlocked.Exchange(ref _clampLogged, 1) == 0)
        {
            _logger.LogWarning(
                "TenantAuthorization:TenantGenerationCacheDuration {Configured} is above {Max} and is clamped: the cache must " +
                "stay below the delete tombstone time, or a recreated tenant could see the deleted tenant's generation (AB#8999)",
                ttl, MaxCacheDuration);
        }

        return MaxCacheDuration;
    }

    private static bool IsUsable(CacheEntry entry, DateTimeOffset now, TimeSpan ttl, bool refresh)
    {
        if (entry.Load.IsFaulted || entry.Load.IsCanceled)
        {
            // a failed read is answered as failed for a short back-off counted from the failure (a slow read that
            // times out still gets the full back-off), then read again
            return entry.FailedAt is { } failedAt && now - failedAt < FailureBackoff;
        }

        if (!entry.Load.IsCompleted)
        {
            // a read is in flight: share it, also for a refresh
            return true;
        }

        var age = now - entry.CreatedAt;
        if (refresh)
        {
            return age < MinRefreshInterval;
        }

        return age < ttl;
    }

    private void Trim(DateTimeOffset now, TimeSpan ttl)
    {
        foreach (var expired in _cache.Where(e => e.Value.Load.IsCompleted && now - e.Value.CreatedAt >= ttl)
                     .Select(e => e.Key).ToList())
        {
            _cache.Remove(expired);
        }

        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }
    }

    private async Task<string?> LoadAsync(string tenantId, CacheEntry entry)
    {
        // Yield first so the read never runs inside the cache lock.
        await Task.Yield();
        TenantLifecycleRecord? record;
        try
        {
            // The shared read is not cancelled by one caller; callers cancel their own wait.
            record = await _lifecycleStore!.GetAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            entry.FailedAt = _timeProvider.GetUtcNow();
            _logger.LogError(
                "The generation of tenant '{TenantId}' could not be read ({ExceptionType}) (AB#8999)",
                tenantId, ex.GetType().Name);
            throw new TenantGenerationLookupException(tenantId, ex);
        }

        return record is null ? null : TenantGenerationFormat.Format(record.CreatedUtc);
    }

    private sealed class CacheEntry(DateTimeOffset createdAt)
    {
        public DateTimeOffset CreatedAt { get; } = createdAt;

        /// <summary>Set once, inside the cache lock, right after the entry is created.</summary>
        public Task<string?> Load { get; set; } = null!;

        /// <summary>When the read failed; written before the task completes, so a reader that sees the fault sees it.</summary>
        public DateTimeOffset? FailedAt { get; set; }
    }
}
