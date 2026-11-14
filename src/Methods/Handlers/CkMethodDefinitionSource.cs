using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>Where the startup validation finds method definitions.</summary>
internal interface ICkMethodDefinitionSource
{
    /// <summary>Tenants searched, for the error message.</summary>
    IReadOnlyList<string> TenantIds { get; }

    /// <summary>
    ///     The method with the qualified (model-version-less) id, or null when no searched tenant has it. Throws
    ///     <see cref="CkMethodDefinitionCacheNotLoadedException" /> when a searched tenant's CK cache is not loaded (any more),
    ///     because then "not found" would be a guess.
    /// </summary>
    CkMethodDto? Find(string qualifiedMethodId);

    /// <summary>
    ///     Makes sure the CK cache of every searched tenant is loaded before <see cref="Find" /> runs (AB#8969): loads it, or
    ///     waits for a running CK model import, until <paramref name="deadline" />. Throws
    ///     <see cref="CkMethodDefinitionCacheNotLoadedException" /> when a cache is still not loaded at the deadline. Sources
    ///     without a cache (tests) need nothing.
    /// </summary>
    Task EnsureLoadedAsync(DateTimeOffset deadline, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
///     The CK cache of a validation tenant is not loaded, so the startup validation cannot tell whether a method exists
///     (AB#8969). Distinct from the "no such method" error of <see cref="CkMethodRegistrationValidator" />.
/// </summary>
internal sealed class CkMethodDefinitionCacheNotLoadedException(string tenantId, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    /// <summary>The tenant whose CK cache is not loaded.</summary>
    public string TenantId { get; } = tenantId;
}

/// <summary>Loads the CK cache of one tenant (AB#8969); the default goes through the tenant context of the service.</summary>
internal interface ICkMethodValidationCacheLoader
{
    /// <summary>Loads the CK cache of <paramref name="tenantId" /> if it is not loaded.</summary>
    Task LoadAsync(string tenantId);
}

/// <summary>
///     Reads the method definitions from the per-tenant CK cache of the service. Before the validation it loads the cache of
///     every validation tenant that is not loaded (AB#8969): a CK model import at startup (in this replica, or the reload after
///     another replica imported, AB#8667) unloads the cache, and nothing else loads it before the validation runs.
/// </summary>
/// <remarks>
///     Shape chosen in AB#8969: <see cref="Find" /> stays synchronous (the validator walks every registration with it), and the
///     asynchronous load/wait runs once before it in <see cref="EnsureLoadedAsync" />. When a cache is unloaded again between the
///     two, <see cref="Find" /> throws <see cref="CkMethodDefinitionCacheNotLoadedException" /> instead of answering "not found",
///     and <see cref="CkMethodRegistrationValidator.ValidateAsync" /> loads it again and repeats, within the same deadline.
/// </remarks>
internal sealed class CkCacheMethodDefinitionSource : ICkMethodDefinitionSource
{
    /// <summary>Pause between two load attempts of one tenant.</summary>
    internal static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(500);

    private readonly ICkCacheService _ckCache;
    private readonly ICkMethodValidationCacheLoader? _loader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public CkCacheMethodDefinitionSource(ICkCacheService ckCache, IOptions<CkMethodHandlerOptions> options,
        IOptions<OctoSystemConfiguration> systemOptions, ICkMethodValidationCacheLoader? loader = null,
        TimeProvider? timeProvider = null, ILogger<CkCacheMethodDefinitionSource>? logger = null)
    {
        _ckCache = ckCache;
        _loader = loader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        // The CK cache is keyed by the lower-case tenant id (the database name, see TenantContext), while the configured
        // system tenant id is mixed-case ("OctoSystem"): normalise the same way, or the lookup misses (AB#7912).
        TenantIds = (options.Value.ValidationTenantIds.Count > 0
                ? options.Value.ValidationTenantIds
                : [systemOptions.Value.SystemTenantId])
            .Select(NormalizeTenantId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Pause between two load attempts of one tenant (tests shorten it).</summary>
    internal TimeSpan RetryInterval { get; init; } = DefaultRetryInterval;

    /// <summary>The key under which the CK cache holds a tenant: trimmed and lower-case.</summary>
    internal static string NormalizeTenantId(string tenantId) => tenantId.Trim().ToLowerInvariant();

    public IReadOnlyList<string> TenantIds { get; }

    public async Task EnsureLoadedAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        foreach (var tenantId in TenantIds)
        {
            await EnsureTenantLoadedAsync(tenantId, deadline, cancellationToken).ConfigureAwait(false);
        }
    }

    public CkMethodDto? Find(string qualifiedMethodId)
    {
        if (!CkMethodIds.TryParse(qualifiedMethodId, out var declaringTypeId, out var methodId))
        {
            return null;
        }

        foreach (var tenantId in TenantIds)
        {
            if (!_ckCache.IsTenantLoaded(tenantId))
            {
                // AB#8969: skipping it would turn an unloaded cache into a false "no method definition"
                throw NotLoaded(tenantId, null);
            }

            try
            {
                if (_ckCache.TryGetRtCkType(tenantId, declaringTypeId, out var type) &&
                    type.AllMethods.TryGetValue(methodId, out var method))
                {
                    return method.Definition;
                }
            }
            catch (CkCacheException ex)
            {
                // unloaded between IsTenantLoaded and the lookup (a concurrent CK model import)
                throw NotLoaded(tenantId, ex);
            }
        }

        return null;
    }

    private async Task EnsureTenantLoadedAsync(string tenantId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1;; attempt++)
        {
            if (_ckCache.IsTenantLoaded(tenantId))
            {
                return;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw NotLoaded(tenantId, lastError);
            }

            if (_loader != null)
            {
                try
                {
                    // the load itself may wait for a running CK model import (AB#8677); never longer than the deadline
                    await _loader.LoadAsync(tenantId).WaitAsync(remaining, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    if (_ckCache.IsTenantLoaded(tenantId))
                    {
                        _logger.LogInformation(
                            "Loaded the CK cache of tenant '{TenantId}' for the CK method validation (attempt {Attempt})",
                            tenantId, attempt);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // e.g. the load ran out waiting for another replica's import (CkModelImportInProgress); retried until the deadline
                    lastError = ex;
                    _logger.LogInformation(ex,
                        "Loading the CK cache of tenant '{TenantId}' for the CK method validation failed (attempt {Attempt}); " +
                        "retrying until {Deadline:O}", tenantId, attempt, deadline);
                }
            }

            // no loader (a host without tenant contexts): wait for whoever loads the cache, within the deadline
            remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static CkMethodDefinitionCacheNotLoadedException NotLoaded(string tenantId, Exception? inner) =>
        new(tenantId,
            $"The CK cache of tenant '{tenantId}' did not load, so the CK method handler registrations cannot be validated " +
            $"against it. The service start stops here; this is not a missing method. Check that the tenant exists and that " +
            $"its CK models resolve, or raise {CkMethodHandlerOptions.SectionName}:" +
            $"{nameof(CkMethodHandlerOptions.CacheLoadTimeout)} when CK model imports of other replicas take longer" +
            (inner == null ? "." : $" (last error: {inner.Message})."), inner);
}
