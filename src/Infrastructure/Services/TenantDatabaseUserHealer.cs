using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     AB#8177 — heals a <b>running</b> tenant whose MongoDB user vanished (restored or re-created MongoDB, manual
///     cleanup). The user is created only on tenant create/attach, so without this every access to the tenant's
///     database fails with MongoDB error 18 (<c>AuthenticationFailed</c>) or 13 (<c>Unauthorized</c>) until the
///     service is restarted. Callers that see such a failure (the stalled-tenant reconciler, the identity probe
///     guard) call <see cref="TryHealAsync"/>; it re-ensures the user through <see cref="ISystemContext" />, which also
///     drops the cached repository clients so pooled connections authenticate anew.
/// </summary>
/// <remarks>
///     Rate-limited per tenant (<see cref="MinimumInterval" />, one minute by default) so a tenant that keeps failing
///     for another reason costs one <c>usersInfo</c> per interval, not one per failed request. Best-effort: a service
///     without admin credentials (error on the admin connection) just logs and goes on as before. The limit is
///     process-wide through <see cref="Default" />.
/// </remarks>
public sealed class TenantDatabaseUserHealer(TimeProvider? timeProvider = null, TimeSpan? minimumInterval = null)
{
    private const int MongoCodeUnauthorized = 13;
    private const int MongoCodeAuthenticationFailed = 18;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The process-wide instance (shared rate limit).</summary>
    public static TenantDatabaseUserHealer Default { get; } = new();

    /// <summary>Shortest time between two heal attempts of the same tenant.</summary>
    public TimeSpan MinimumInterval { get; } = minimumInterval ?? TimeSpan.FromMinutes(1);

    /// <summary>
    ///     True for the failures a missing or rejected tenant database user causes: MongoDB error 18 / 13 (also when
    ///     wrapped in write / bulk-write exceptions or inner exceptions) or a failed SCRAM authentication.
    /// </summary>
    public static bool IsAuthenticationFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case MongoAuthenticationException:
                    return true;
                case MongoCommandException command when IsAuthCode(command.Code):
                    return true;
                case MongoWriteException write when IsAuthCode(write.WriteError?.Code):
                    return true;
                case MongoBulkWriteException bulk when bulk.WriteErrors.Any(e => IsAuthCode(e.Code)):
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(IsAuthenticationFailure):
                    return true;
            }
        }

        return false;
    }

    private static bool IsAuthCode(int? code) => code is MongoCodeUnauthorized or MongoCodeAuthenticationFailed;

    /// <summary>
    ///     Re-ensures the MongoDB user of a registered tenant, at most once per <see cref="MinimumInterval" />.
    /// </summary>
    /// <returns>
    ///     The ensure result, or <c>null</c> when the attempt was skipped by the rate limit or failed (logged).
    /// </returns>
    public async Task<TenantDatabaseUserEnsureResult?> TryHealAsync(ISystemContext systemContext, string tenantId,
        ILogger logger)
    {
        if (!TryReserve(tenantId))
        {
            return null;
        }

        try
        {
            var result = await systemContext.EnsureTenantDatabaseUserAsync(tenantId).ConfigureAwait(false);
            if (result == TenantDatabaseUserEnsureResult.Created)
            {
                logger.LogWarning(
                    "Re-created the missing MongoDB user of running tenant '{TenantId}'; its database is reachable again " +
                    "without a restart", tenantId);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not verify or re-create the MongoDB user of tenant '{TenantId}' (this service may lack admin " +
                "access to MongoDB); next attempt in {Interval:F0} s", tenantId, MinimumInterval.TotalSeconds);
            return null;
        }
    }

    private bool TryReserve(string tenantId)
    {
        var now = _time.GetUtcNow();
        lock (_lastAttempt)
        {
            if (_lastAttempt.TryGetValue(tenantId, out var last) && now - last < MinimumInterval)
            {
                return false;
            }

            _lastAttempt[tenantId] = now;
            return true;
        }
    }
}
