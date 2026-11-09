using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     In-memory <see cref="ICkMethodOperationStore" /> for tests and single-replica hosts: the records are not shared
///     between replicas and do not survive a restart, so it does <b>not</b> meet the durability rule of OD-5. Production
///     hosts use the MongoDB store (<c>Meshmakers.Octo.Services.Methods.MongoDb</c>).
/// </summary>
public sealed class CkMethodInMemoryOperationStore(TimeProvider timeProvider) : ICkMethodOperationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string TenantId, string OperationId), CkMethodOperationRecord> _records = new();

    /// <summary>Number of records held, expired ones included (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var stored = record with { TenantId = Normalize(record.TenantId), State = CkMethodOperationState.Accepted };
        lock (_gate)
        {
            if (!_records.TryAdd((stored.TenantId, stored.OperationId), stored))
            {
                throw new CkMethodOperationStoreException("An operation with this id exists in the tenant.");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            return Task.FromResult(_records.TryGetValue((Normalize(tenantId), operationId), out var record) &&
                                   !record.IsExpired(now)
                ? record
                : null);
        }
    }

    /// <inheritdoc />
    public Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var tenantId = Normalize(query.TenantId);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var matches = _records.Values
                .Where(r => r.TenantId == tenantId && !r.IsExpired(now))
                .Where(r => Matches(r, query))
                .OrderByDescending(r => r.AcceptedAtUtc)
                .ThenBy(r => r.OperationId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(new CkMethodOperationPage(
                matches.Skip(query.Skip).Take(query.Take).ToList(), matches.Count));
        }
    }

    /// <inheritdoc />
    public Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Update(tenantId, operationId, r => r.State == CkMethodOperationState.Accepted,
            r => r with { State = CkMethodOperationState.Running, StartedAtUtc = nowUtc, HeartbeatAtUtc = nowUtc });

    /// <inheritdoc />
    public Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Validate();
        return Update(tenantId, operationId, r => !r.IsFinal,
            r => r with { Progress = progress, HeartbeatAtUtc = nowUtc });
    }

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Update(tenantId, operationId, r => !r.IsFinal, r => r with { HeartbeatAtUtc = nowUtc });

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        completion.Validate();
        return Update(tenantId, operationId, r => !r.IsFinal, r => r with
        {
            State = completion.State,
            ResultJson = completion.ResultJson,
            Error = completion.Error,
            CompletedAtUtc = completion.CompletedAtUtc,
            ExpiresAtUtc = completion.ExpiresAtUtc
        });
    }

    /// <inheritdoc />
    public Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        var error = HandlerLostError();
        var count = 0;
        lock (_gate)
        {
            foreach (var (key, record) in _records.ToList())
            {
                if (record.IsFinal || record.HeartbeatAtUtc >= staleBeforeUtc)
                {
                    continue;
                }

                _records[key] = record with
                {
                    State = CkMethodOperationState.Failed,
                    Error = error with
                    {
                        Parameters = new Dictionary<string, string>(error.Parameters)
                        {
                            [CkMethodErrorParameters.MethodId] = record.MethodId
                        }
                    },
                    CompletedAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc + retention
                };
                count++;
            }
        }

        return Task.FromResult(count);
    }

    /// <summary>The error a lost handler ends with (without the method id, which the store adds per record).</summary>
    internal static CkMethodError HandlerLostError() =>
        CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
            (CkMethodErrorParameters.Reason, CkMethodOperationReasons.HandlerLost));

    internal static string Normalize(string tenantId) => CkMethodOperationTenant.Normalize(tenantId);

    private static bool Matches(CkMethodOperationRecord record, CkMethodOperationQuery query)
    {
        if (query.Owner is { } owner)
        {
            var ownerMatches = owner.SubjectId is not null
                ? string.Equals(record.Owner.SubjectId, owner.SubjectId, StringComparison.Ordinal)
                : record.Owner.SubjectId is null &&
                  string.Equals(record.Owner.ClientId, owner.ClientId, StringComparison.Ordinal);
            if (!ownerMatches)
            {
                return false;
            }
        }

        return (query.MethodId is null || string.Equals(record.MethodId, query.MethodId, StringComparison.Ordinal)) &&
               (query.State is null || record.State == query.State) &&
               (query.SinceUtc is null || record.AcceptedAtUtc >= query.SinceUtc);
    }

    private Task<bool> Update(string tenantId, string operationId, Func<CkMethodOperationRecord, bool> condition,
        Func<CkMethodOperationRecord, CkMethodOperationRecord> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var key = (Normalize(tenantId), operationId);
        lock (_gate)
        {
            if (!_records.TryGetValue(key, out var record) || !condition(record))
            {
                return Task.FromResult(false);
            }

            _records[key] = change(record);
            return Task.FromResult(true);
        }
    }
}
