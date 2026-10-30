using System.Collections.Concurrent;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     In-memory <see cref="ICkMethodIdempotencyStore" /> for tests and single-replica hosts. The state is per process: two
///     gateway replicas do not share it, so a multi-replica host needs the MongoDB store (AB#7824). Records are kept until
///     they expire; expired records are removed opportunistically while claims arrive and are never read as live.
/// </summary>
public sealed class CkMethodInMemoryIdempotencyStore(CkMethodIdempotencyLifetime lifetime, TimeProvider timeProvider)
    : ICkMethodIdempotencyStore
{
    private const int SweepEvery = 256;
    private readonly ConcurrentDictionary<string, CkMethodIdempotencyRecord> _records = new(StringComparer.Ordinal);
    private int _claims;

    /// <summary>Number of records held, expired ones included until swept (for tests).</summary>
    public int Count => _records.Count;

    /// <inheritdoc />
    public Task<CkMethodIdempotencyClaimResult> ClaimAsync(CkMethodIdempotencyClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (Interlocked.Increment(ref _claims) % SweepEvery == 0)
        {
            Sweep(now);
        }

        var id = request.Key.Id;
        var fresh = new CkMethodIdempotencyRecord
        {
            Id = id,
            State = CkMethodIdempotencyState.InProgress,
            MethodId = request.Key.MethodId,
            FirstInvocationId = request.FirstInvocationId,
            Fingerprint = request.Fingerprint.ToArray(),
            FingerprintKeyId = request.FingerprintKeyId,
            ClaimedAt = now,
            LeaseUntil = request.LeaseUntil,
            ExpiresAt = now + lifetime.Ttl
        };

        while (true)
        {
            if (_records.TryAdd(id, fresh))
            {
                return Task.FromResult(CkMethodIdempotencyClaimResult.ClaimedBy(fresh));
            }

            if (!_records.TryGetValue(id, out var existing))
            {
                continue; // removed between the two calls: try again
            }

            if (!existing.IsExpired(now))
            {
                return Task.FromResult(CkMethodIdempotencyClaimResult.ExistingRecord(existing));
            }

            // takeover of an expired record: a conditional replace, only if it is still the record that was read
            if (_records.TryUpdate(id, fresh, existing))
            {
                return Task.FromResult(CkMethodIdempotencyClaimResult.ClaimedBy(fresh));
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string id, Guid firstInvocationId, CkMethodIdempotencyState state,
        CkMethodResponse? response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (state == CkMethodIdempotencyState.InProgress)
        {
            throw new ArgumentException("A claim is completed as Completed or Unknown.", nameof(state));
        }

        if (state == CkMethodIdempotencyState.Completed && response is null)
        {
            throw new ArgumentException("A completed record needs the response envelope to replay.", nameof(response));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        while (true)
        {
            if (!_records.TryGetValue(id, out var existing) || existing.IsExpired(now) ||
                existing.FirstInvocationId != firstInvocationId || existing.State != CkMethodIdempotencyState.InProgress)
            {
                return Task.FromResult(false);
            }

            var settled = existing with
            {
                State = state,
                Response = state == CkMethodIdempotencyState.Completed ? response : null
            };
            if (_records.TryUpdate(id, settled, existing))
            {
                return Task.FromResult(true);
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(string id, Guid firstInvocationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        while (true)
        {
            if (!_records.TryGetValue(id, out var existing) || existing.IsExpired(now) ||
                existing.FirstInvocationId != firstInvocationId || existing.State != CkMethodIdempotencyState.InProgress)
            {
                return Task.FromResult(false);
            }

            // conditional remove: only the record that was read
            if (_records.TryRemove(new KeyValuePair<string, CkMethodIdempotencyRecord>(id, existing)))
            {
                return Task.FromResult(true);
            }
        }
    }

    private void Sweep(DateTime now)
    {
        foreach (var pair in _records)
        {
            if (pair.Value.IsExpired(now))
            {
                _records.TryRemove(pair);
            }
        }
    }
}
