using System.Collections.Concurrent;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Thread-safe registry tracking tenant IDs that failed during deferred startup.
///     Used by the background retry service to periodically re-attempt startup for these tenants.
/// </summary>
public class FailedTenantRegistry
{
    private readonly ConcurrentDictionary<string, int> _failedTenants = new();

    public void Add(string tenantId) => _failedTenants.TryAdd(tenantId, 0);

    public bool Remove(string tenantId) => _failedTenants.TryRemove(tenantId, out _);

    public int IncrementRetryCount(string tenantId)
    {
        return _failedTenants.AddOrUpdate(tenantId, 1, (_, count) => count + 1);
    }

    public IReadOnlyList<KeyValuePair<string, int>> GetAll() => _failedTenants.ToArray();

    public bool HasFailedTenants => !_failedTenants.IsEmpty;
}
