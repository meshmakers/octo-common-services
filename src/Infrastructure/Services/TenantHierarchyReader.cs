using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Default <see cref="ITenantHierarchyReader" />: reads the parent tenant's own registry through
///     <see cref="ISystemContext" /> and caches the answer for
///     <see cref="TenantAuthorizationOptions.TenantHierarchyCacheDuration" />.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ISystemContext" /> is the dependency every host of this package already
///         registers (<c>AddRuntimeEngine()</c>) and that <c>TenantMiddleware</c> already uses per
///         request, so the parent-tenant rule needs no new dependency. The lookup itself is
///         <see cref="ITenantContext.IsChildTenantExistingAsync" /> — one indexed query against the
///         parent's registry — deliberately not <c>TryGetChildTenantContextAsync</c>, whose resolve
///         constructs a tenant context and runs the CK model auto-imports (the same reason
///         <c>IsTenantRegisteredAsync</c> exists, AB#4829).
///     </para>
///     <para>
///         <b>Why one level and not a subtree walk.</b> Only the downward direction is stored, so a
///         subtree check is a breadth-first descent whose <i>width</i> is unbounded: every intermediate
///         node has to be materialised as a tenant context (CK auto-imports, connection pool) before
///         its own children can be listed. Capping the depth does not cap that; capping the width would
///         make an authorization answer depend on registry enumeration order. The deep case that
///         actually occurs — a platform operator authenticated in the system tenant — is covered
///         exactly, because the system tenant's registry <i>is</i> the platform-wide one. If a
///         mid-level parent ever needs its grandchildren, the honest fix is an upward walk over the
///         system registry's <c>ParentTenantId</c> (O(depth), no fan-out), which needs an engine API
///         that does not exist today.
///     </para>
/// </remarks>
public class TenantHierarchyReader : ITenantHierarchyReader
{
    /// <summary>
    ///     Upper bound on cached pairs. The route tenant of a request is attacker-controlled, so an
    ///     unbounded cache is a memory amplifier; when the cap is reached expired entries are dropped
    ///     and, if that is not enough, the cache is emptied. Worst case that costs one re-resolution
    ///     per live pair, and a real estate has orders of magnitude fewer pairs than the cap.
    /// </summary>
    private const int MaxCacheEntries = 1024;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TenantHierarchyReader> _logger;
    private readonly IOptions<TenantAuthorizationOptions> _options;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Creates a new instance of <see cref="TenantHierarchyReader" />.
    /// </summary>
    public TenantHierarchyReader(ISystemContext systemContext, IOptions<TenantAuthorizationOptions> options,
        ILogger<TenantHierarchyReader> logger)
    {
        _systemContext = systemContext;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsChildTenantAsync(string parentTenantId, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(parentTenantId), parentTenantId);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        // A tenant is not its own child. Callers are expected to have handled equality already (it is
        // the common case and must cost nothing), but answering it here keeps the contract total.
        if (string.Equals(parentTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cacheDuration = _options.Value.TenantHierarchyCacheDuration;
        var key = BuildKey(parentTenantId, tenantId);
        var now = DateTimeOffset.UtcNow;

        if (cacheDuration > TimeSpan.Zero && _cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.IsChild;
        }

        var isChild = await ResolveIsChildTenantAsync(parentTenantId, tenantId).ConfigureAwait(false);

        if (cacheDuration > TimeSpan.Zero)
        {
            TrimCacheIfNeeded(now);
            _cache[key] = new CacheEntry(isChild, now + cacheDuration);
        }

        return isChild;
    }

    private async Task<bool> ResolveIsChildTenantAsync(string parentTenantId, string tenantId)
    {
        try
        {
            var parentContext = await _systemContext.TryFindTenantContextAsync(parentTenantId).ConfigureAwait(false);
            if (parentContext == null)
            {
                _logger.LogDebug(
                    "Tenant '{ParentTenantId}' does not exist, so tenant '{TenantId}' is not one of its children",
                    parentTenantId, tenantId);
                return false;
            }

            using var session = await parentContext.GetAdminSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            try
            {
                var isChild = await parentContext.IsChildTenantExistingAsync(session, tenantId)
                    .ConfigureAwait(false);
                await session.CommitTransactionAsync().ConfigureAwait(false);

                _logger.LogDebug(
                    "Resolved tenant hierarchy: '{TenantId}' {Relation} a child of '{ParentTenantId}'",
                    tenantId, isChild ? "is" : "is not", parentTenantId);
                return isChild;
            }
            catch
            {
                await session.AbortTransactionAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception e)
        {
            // Fail closed: an unreadable hierarchy is not a relationship. Never let this turn a
            // request that the exact tenant match would have answered with 403 into a 500.
            _logger.LogWarning(e,
                "Could not resolve whether tenant '{TenantId}' is a child of '{ParentTenantId}'; treating the two as unrelated",
                tenantId, parentTenantId);
            return false;
        }
    }

    private static string BuildKey(string parentTenantId, string tenantId)
    {
        // The unit separator cannot occur in a tenant id, so no pair of ids can collide with another.
        return string.Concat(parentTenantId, "\u001f", tenantId);
    }

    private void TrimCacheIfNeeded(DateTimeOffset now)
    {
        if (_cache.Count < MaxCacheEntries)
        {
            return;
        }

        foreach (var entry in _cache)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _cache.TryRemove(entry.Key, out _);
            }
        }

        if (_cache.Count >= MaxCacheEntries)
        {
            _logger.LogDebug("Tenant hierarchy cache exceeded {MaxCacheEntries} live entries and was cleared",
                MaxCacheEntries);
            _cache.Clear();
        }
    }

    private readonly record struct CacheEntry(bool IsChild, DateTimeOffset ExpiresAt);
}
