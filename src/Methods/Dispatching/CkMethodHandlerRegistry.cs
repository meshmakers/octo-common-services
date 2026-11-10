using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     In-memory handler registry (singleton). Expiry uses the dispatcher's clock at receipt (receipt time plus the
///     announced TTL), so clock skew between a handler host and the dispatcher cannot expire a live handler early.
///     A withdrawal wins over every announcement of the same instance that is not newer by the handler's own
///     timestamps, so a late heartbeat cannot resurrect a stopped instance. Entries are keyed by the queue name of the
///     method (<see cref="CkMethodQueueNames.For" />: model-version-less, case-insensitive), the same key the send uses.
///     With persistence enabled (<see cref="EnablePersistence" />, AB#5671) every change is queued for the snapshot store
///     and a restarted host restores the rows with <see cref="Restore" />; a restored row is a "last seen" record and
///     counts as live only inside its heartbeat TTL window, exactly like a received announcement.
///     The registry is the gate of the bus send (AB#8634): it knows when its warm-up ends (<see cref="WarmUpEndsUtc" />)
///     and signals every announcement and withdrawal (<see cref="WaitForChangeAsync" />), so the dispatcher can wait for
///     a handler that announces itself right after a restart instead of refusing it.
/// </summary>
internal sealed class CkMethodHandlerRegistry : ICkMethodHandlerRegistry
{
    internal static readonly TimeSpan LastSeenRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Dictionary<string, State>> _methods = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly HashSet<(string MethodKey, string InstanceId)> _dirty = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _warmUp;
    private bool _trackChanges;
    private DateTime _lastPruneUtc;
    private DateTime _warmUpEndsUtc;
    private TaskCompletionSource _changed = NewSignal();

    public CkMethodHandlerRegistry(TimeProvider timeProvider, IOptions<CkMethodDispatcherOptions>? options = null)
    {
        _timeProvider = timeProvider;
        var warmUp = (options?.Value ?? new CkMethodDispatcherOptions()).RegistryWarmUp;
        _warmUp = warmUp > TimeSpan.Zero ? warmUp : TimeSpan.Zero;
        _warmUpEndsUtc = Now + _warmUp;
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    ///     Until then an empty registry may still fill (dispatcher just started or just asked for discovery): the dispatcher
    ///     waits for an announcement instead of refusing. Starts with the registry and is extended by
    ///     <see cref="ExtendWarmUp" />.
    /// </summary>
    internal DateTime WarmUpEndsUtc
    {
        get
        {
            lock (_lock)
            {
                return _warmUpEndsUtc;
            }
        }
    }

    /// <summary>Starts the warm-up again from now (the dispatcher asked every handler to announce itself); never shortens it.</summary>
    internal void ExtendWarmUp()
    {
        var end = Now + _warmUp;
        lock (_lock)
        {
            if (end > _warmUpEndsUtc)
            {
                _warmUpEndsUtc = end;
            }
        }
    }

    /// <summary>
    ///     Completes with the next announcement or withdrawal received after the call (also one that changes nothing).
    ///     Take the task before checking <see cref="GetLiveHandlers" /> again, so a message in between is not missed.
    /// </summary>
    internal Task WaitForChangeAsync()
    {
        lock (_lock)
        {
            return _changed.Task;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void OnAnnounced(CkMethodHandlerAnnounced message)
    {
        var now = Now;
        var ttl = TimeSpan.FromSeconds(Math.Clamp(message.TtlSeconds, 1, 24 * 3600));
        IReadOnlyList<string> tenants = message.Tenants.Count == 0 ? [CkMethodHandlerTenants.All] : message.Tenants.ToList();
        TaskCompletionSource signal;
        lock (_lock)
        {
            foreach (var methodId in message.MethodIds)
            {
                var instances = InstancesOf(Normalize(methodId));
                if (instances.TryGetValue(message.InstanceId, out var existing) &&
                    existing.WithdrawnAtSource is { } withdrawnAt && message.Timestamp <= withdrawnAt)
                {
                    continue; // stale announcement delivered after the withdrawal
                }

                instances[message.InstanceId] = new State(
                    new CkMethodHandlerEntry(methodId, message.ServiceName, message.InstanceId, message.Version,
                        message.ContractVersion, now, now + ttl, false, tenants), null);
                MarkDirty(Normalize(methodId), message.InstanceId);
            }

            Prune(now);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    internal void OnWithdrawn(CkMethodHandlerWithdrawn message)
    {
        var now = Now;
        TaskCompletionSource signal;
        lock (_lock)
        {
            foreach (var methodId in message.MethodIds)
            {
                var instances = InstancesOf(Normalize(methodId));
                var entry = instances.TryGetValue(message.InstanceId, out var existing)
                    ? existing.Entry with
                    {
                        LastSeenUtc = now, ExpiresUtc = now, Withdrawn = true, Source = CkMethodHandlerEntrySource.Announcement
                    }
                    : new CkMethodHandlerEntry(methodId, message.ServiceName, message.InstanceId, null,
                        CkMethodContract.CurrentVersion, now, now, true, [CkMethodHandlerTenants.All]);
                // Keeps the newest source timestamp: an older withdrawal must not let an older announcement in.
                var withdrawnAt = existing?.WithdrawnAtSource is { } previous && previous > message.Timestamp
                    ? previous
                    : message.Timestamp;
                instances[message.InstanceId] = new State(entry, withdrawnAt);
                MarkDirty(Normalize(methodId), message.InstanceId);
            }

            signal = _changed;
            _changed = NewSignal();
        }

        // a waiting call re-checks and is refused at once (a withdrawal is an answer)
        signal.TrySetResult();
    }

    /// <summary>
    ///     The broker returned a mandatory send (no queue bound): expires the instances of the method that were last
    ///     seen before the send started. An instance announced while the send was in flight (rolling update) stays live.
    /// </summary>
    internal void MarkNoQueueBound(string qualifiedMethodId, DateTime sendStartedUtc)
    {
        var now = Now;
        lock (_lock)
        {
            if (!_methods.TryGetValue(Normalize(qualifiedMethodId), out var instances))
            {
                return;
            }

            foreach (var key in instances.Keys.ToList())
            {
                var state = instances[key];
                if (state.Entry.ExpiresUtc > now && state.Entry.LastSeenUtc <= sendStartedUtc)
                {
                    instances[key] = state with { Entry = state.Entry with { ExpiresUtc = now, NoQueueBound = true } };
                    MarkDirty(Normalize(qualifiedMethodId), key);
                }
            }
        }
    }

    /// <summary>Starts queueing every change for <see cref="TakeChanges" /> (called once a snapshot store exists).</summary>
    internal void EnablePersistence()
    {
        lock (_lock)
        {
            _trackChanges = true;
        }
    }

    /// <summary>
    ///     Merges persisted rows into the registry (startup of a restarted host). A row never replaces an entry that was
    ///     received since (newer or equal <c>LastSeenUtc</c> in memory wins), a withdrawn row stays a tombstone, and the
    ///     times are bounded: a row cannot claim to be seen in the future or to live longer than the maximum TTL.
    ///     Restored rows are not queued for writing back.
    /// </summary>
    /// <returns>The number of rows taken over.</returns>
    internal int Restore(IEnumerable<CkMethodHandlerSnapshotRecord> records)
    {
        var now = Now;
        var restored = 0;
        lock (_lock)
        {
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.MethodKey) || string.IsNullOrEmpty(record.InstanceId))
                {
                    continue;
                }

                var lastSeen = record.LastSeenUtc > now ? now : record.LastSeenUtc;
                if (lastSeen + LastSeenRetention < now)
                {
                    continue; // history older than the registry keeps
                }

                var maxExpiry = lastSeen + MaxTtl;
                var expires = record.ExpiresUtc > maxExpiry ? maxExpiry : record.ExpiresUtc;
                var instances = InstancesOf(record.MethodKey);
                if (instances.TryGetValue(record.InstanceId, out var existing) && existing.Entry.LastSeenUtc >= lastSeen)
                {
                    continue;
                }

                var entry = new CkMethodHandlerEntry(record.MethodId, record.ServiceName, record.InstanceId,
                    record.Version, record.ContractVersion, lastSeen, record.Withdrawn ? lastSeen : expires,
                    record.Withdrawn, record.Tenants.ToList()) { NoQueueBound = record.NoQueueBound, Source = CkMethodHandlerEntrySource.Snapshot };
                instances[record.InstanceId] = new State(entry, record.Withdrawn ? record.WithdrawnAtSource : null);
                restored++;
            }
        }

        return restored;
    }

    /// <summary>Takes the rows changed since the last call (empty while persistence is not enabled).</summary>
    internal IReadOnlyList<CkMethodHandlerSnapshotRecord> TakeChanges()
    {
        lock (_lock)
        {
            if (_dirty.Count == 0)
            {
                return [];
            }

            var rows = new List<CkMethodHandlerSnapshotRecord>(_dirty.Count);
            foreach (var (methodKey, instanceId) in _dirty)
            {
                if (_methods.TryGetValue(methodKey, out var instances) && instances.TryGetValue(instanceId, out var state))
                {
                    rows.Add(ToRecord(methodKey, state));
                }
            }

            _dirty.Clear();
            return rows;
        }
    }

    /// <summary>Queues rows again after a failed write.</summary>
    internal void Requeue(IEnumerable<CkMethodHandlerSnapshotRecord> records)
    {
        lock (_lock)
        {
            foreach (var record in records)
            {
                MarkDirty(record.MethodKey, record.InstanceId);
            }
        }
    }

    private void MarkDirty(string methodKey, string instanceId)
    {
        if (_trackChanges)
        {
            _dirty.Add((methodKey, instanceId));
        }
    }

    private static CkMethodHandlerSnapshotRecord ToRecord(string methodKey, State state)
    {
        var e = state.Entry;
        return new CkMethodHandlerSnapshotRecord
        {
            MethodKey = methodKey,
            InstanceId = e.InstanceId,
            MethodId = e.MethodId,
            ServiceName = e.ServiceName,
            Version = e.Version,
            ContractVersion = e.ContractVersion,
            LastSeenUtc = e.LastSeenUtc,
            ExpiresUtc = e.ExpiresUtc,
            Withdrawn = e.Withdrawn,
            WithdrawnAtSource = state.WithdrawnAtSource,
            NoQueueBound = e.NoQueueBound,
            Tenants = e.Tenants.ToList(),
            RetainUntilUtc = e.LastSeenUtc + LastSeenRetention
        };
    }

    public IReadOnlyList<CkMethodHandlerEntry> GetLiveHandlers(string qualifiedMethodId)
    {
        var now = Now;
        lock (_lock)
        {
            if (!_methods.TryGetValue(Normalize(qualifiedMethodId), out var instances))
            {
                return [];
            }

            return instances.Values.Select(s => s.Entry)
                .Where(e => !e.Withdrawn && e.ExpiresUtc > now)
                .OrderByDescending(e => e.LastSeenUtc)
                .ToList();
        }
    }

    public CkMethodHandlerEntry? GetLastSeen(string qualifiedMethodId)
    {
        lock (_lock)
        {
            return _methods.TryGetValue(Normalize(qualifiedMethodId), out var instances)
                ? instances.Values.Select(s => s.Entry).MaxBy(e => e.LastSeenUtc)
                : null;
        }
    }

    public IReadOnlyCollection<CkMethodHandlerEntry> Snapshot()
    {
        lock (_lock)
        {
            return _methods.Values.SelectMany(i => i.Values).Select(s => s.Entry).ToList();
        }
    }

    /// <summary>True when the entry serves the tenant.</summary>
    internal static bool Serves(CkMethodHandlerEntry entry, string tenantId) =>
        entry.Tenants.Any(t => t == CkMethodHandlerTenants.All || string.Equals(t, tenantId, StringComparison.Ordinal));

    private Dictionary<string, State> InstancesOf(string key)
    {
        if (!_methods.TryGetValue(key, out var instances))
        {
            instances = new Dictionary<string, State>(StringComparer.Ordinal);
            _methods[key] = instances;
        }

        return instances;
    }

    internal static string Normalize(string methodId)
    {
        try
        {
            return CkMethodQueueNames.For(methodId);
        }
        catch (ArgumentException)
        {
            return methodId;
        }
    }

    private void Prune(DateTime now)
    {
        if (now - _lastPruneUtc < PruneInterval)
        {
            return;
        }

        _lastPruneUtc = now;
        foreach (var (methodKey, instances) in _methods.ToList())
        {
            foreach (var key in instances.Where(kv => kv.Value.Entry.LastSeenUtc + LastSeenRetention < now)
                         .Select(kv => kv.Key).ToList())
            {
                instances.Remove(key);
            }

            if (instances.Count == 0)
            {
                _methods.Remove(methodKey);
            }
        }
    }

    private sealed record State(CkMethodHandlerEntry Entry, DateTime? WithdrawnAtSource);
}
