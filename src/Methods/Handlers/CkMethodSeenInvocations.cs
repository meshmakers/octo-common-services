using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     The one place that decides whether a delivered <see cref="CkMethodRequest" /> must not run again (OD-3 option A,
///     AB#6921): the broker flag of <see cref="CommandDeliveryInfo" /> and, as a second line, a per-instance memory of
///     invocation ids. Every host of method handlers (the library's bus consumer, the communication controller, other
///     services) uses this helper instead of keeping its own copy.
/// </summary>
/// <remarks>
///     The id is remembered before the broker flag is evaluated, so a refused redelivery is remembered as well.
///     The memory keeps an id until <c>min(DeadlineUtc, now + 1 h) + 5 min</c>, capped at <c>now + 1 h</c>. The deadline
///     is clamped before the retention is added: it comes from the wire and may be <see cref="DateTime.MaxValue" />.
///     The memory is per instance and not persisted; the idempotency key of F3.5 covers methods that need more.
///     Thread-safe; register one instance per host as a singleton. The helper never logs or keeps parameters.
/// </remarks>
public sealed class CkMethodSeenInvocations
{
    private const int CleanupEveryAdds = 256;
    private static readonly TimeSpan RetentionAfterDeadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, DateTime> _seen = new();
    private readonly TimeProvider _timeProvider;
    private long _adds;

    /// <summary>Creates the helper.</summary>
    /// <param name="timeProvider">Clock; <see cref="TimeProvider.System" /> when <c>null</c>.</param>
    public CkMethodSeenInvocations(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of invocation ids currently remembered (diagnostics, tests).</summary>
    public int Count => _seen.Count;

    /// <summary>Decides for one delivery and remembers its invocation id.</summary>
    /// <param name="request">The delivered request (only <c>InvocationId</c> and <c>DeadlineUtc</c> are read).</param>
    /// <param name="delivery">Transport delivery info, <c>null</c> for transports without it.</param>
    public CkMethodDeliveryDecision Decide(CkMethodRequest request, CommandDeliveryInfo? delivery = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Marked first so that a refused redelivery is remembered as well.
        var firstSight = TryMarkSeen(request.InvocationId, request.DeadlineUtc);
        if (delivery is { IsPossibleDuplicate: true })
        {
            return CkMethodDeliveryDecision.PossibleDuplicate;
        }

        return firstSight ? CkMethodDeliveryDecision.Fresh : CkMethodDeliveryDecision.AlreadySeen;
    }

    private bool TryMarkSeen(Guid invocationId, DateTime deadlineUtc)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Clamp before the addition: deadline + retention would throw for DateTime.MaxValue.
        var latest = now + MaxRetention;
        var clampedDeadline = deadlineUtc > latest ? latest : deadlineUtc;
        var keepUntil = clampedDeadline + RetentionAfterDeadline;
        if (keepUntil > latest)
        {
            keepUntil = latest;
        }

        if (!_seen.TryAdd(invocationId, keepUntil))
        {
            return false;
        }

        if (Interlocked.Increment(ref _adds) % CleanupEveryAdds == 0)
        {
            foreach (var entry in _seen.Where(e => e.Value < now))
            {
                _seen.TryRemove(entry.Key, out _);
            }
        }

        return true;
    }
}
