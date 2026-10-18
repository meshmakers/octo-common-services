using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Reads the timeout of a CK method from the definition in the CK cache and turns it into the absolute deadline of a
///     <see cref="Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods.CkMethodRequest" />. A gateway uses it
///     to fill <c>DeadlineUtc</c> and <c>TimeoutSeconds</c> of the request it hands to <see cref="ICkMethodDispatcher" />.
/// </summary>
/// <remarks>
///     The CK compiler allows 1-300 seconds (<c>execution.timeoutSeconds</c>, default 15). A value outside that range
///     cannot occur in a compiled model; it is clamped defensively. Nothing here throws: an unknown tenant, type or method
///     and a cache fault give the default timeout, and an absurd timeout (<see cref="TimeSpan.MaxValue" />) is clamped to
///     <see cref="CkMethodDispatcherOptions.MaxTimeout" />.
/// </remarks>
public interface ICkMethodTimeoutResolver
{
    /// <summary>
    ///     The timeout of the method if the CK cache of the tenant knows it.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ckTypeId">The CK type id of the target, e.g. <c>System.Identity/User</c>.</param>
    /// <param name="methodId">The qualified (<c>System.Identity/User.ChangePassword-1</c>) or plain (<c>ChangePassword-1</c>) method id.</param>
    /// <param name="timeout">The timeout, 1-300 s.</param>
    /// <returns>False when the CK cache does not know the method (nothing was clamped or defaulted).</returns>
    bool TryGetTimeout(string tenantId, string ckTypeId, string methodId, out TimeSpan timeout);

    /// <summary>The timeout of the method, or <see cref="CkMethodExecutionDto.DefaultTimeoutSeconds" /> seconds when it is not known.</summary>
    TimeSpan GetTimeout(string tenantId, string ckTypeId, string methodId);

    /// <summary>Now plus <see cref="GetTimeout" />; never throws.</summary>
    DateTime GetDeadlineUtc(string tenantId, string ckTypeId, string methodId);
}

/// <summary>Clamping rules for deadlines and timeouts (no input can overflow <see cref="DateTime" />).</summary>
internal static class CkMethodDeadline
{
    /// <summary>The platform limits of <c>execution.timeoutSeconds</c> (CK compiler).</summary>
    internal const int MinTimeoutSeconds = 1;

    internal const int MaxTimeoutSeconds = 300;

    /// <summary>
    ///     <paramref name="nowUtc" /> plus <paramref name="timeout" />, where a non-positive timeout gives
    ///     <paramref name="nowUtc" /> (already expired) and a timeout above <paramref name="max" /> is cut to it.
    /// </summary>
    internal static DateTime FromTimeout(DateTime nowUtc, TimeSpan timeout, TimeSpan max)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return nowUtc;
        }

        return nowUtc + (timeout > max ? max : timeout);
    }

    /// <summary>The earlier of <paramref name="deadlineUtc" /> and <paramref name="nowUtc" /> + <paramref name="max" />.</summary>
    internal static DateTime Clamp(DateTime deadlineUtc, DateTime nowUtc, TimeSpan max)
    {
        var latest = nowUtc + max;
        return deadlineUtc > latest ? latest : deadlineUtc;
    }

    internal static int ClampSeconds(int seconds) => Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>The dispatcher option, or the 10 minute default when the option is not positive.</summary>
    internal static TimeSpan MaxOf(CkMethodDispatcherOptions options) =>
        options.MaxTimeout > TimeSpan.Zero ? options.MaxTimeout : TimeSpan.FromMinutes(10);
}

internal sealed class CkCacheMethodTimeoutResolver(
    IOptions<CkMethodDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<CkCacheMethodTimeoutResolver> logger,
    ICkCacheService? ckCache = null) : ICkMethodTimeoutResolver
{
    public bool TryGetTimeout(string tenantId, string ckTypeId, string methodId, out TimeSpan timeout)
    {
        timeout = default;
        if (ckCache == null || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(methodId))
        {
            return false;
        }

        try
        {
            var plainMethodId = CkMethodIds.TryParse(methodId, out _, out var parsed) ? parsed : methodId;
            if (!ckCache.IsTenantLoaded(tenantId) ||
                !ckCache.TryGetRtCkType(tenantId, new RtCkId<CkTypeId>(ckTypeId), out var type) ||
                !type.AllMethods.TryGetValue(plainMethodId, out var method))
            {
                return false;
            }

            timeout = TimeSpan.FromSeconds(CkMethodDeadline.ClampSeconds(method.TimeoutSeconds));
            return true;
        }
        catch (Exception e)
        {
            // A malformed type id or a cache fault must not fail the call.
            logger.LogDebug(e, "Reading the timeout of CK method {MethodId} for type {CkTypeId} failed", methodId, ckTypeId);
            return false;
        }
    }

    public TimeSpan GetTimeout(string tenantId, string ckTypeId, string methodId) =>
        TryGetTimeout(tenantId, ckTypeId, methodId, out var timeout)
            ? timeout
            : TimeSpan.FromSeconds(CkMethodExecutionDto.DefaultTimeoutSeconds);

    public DateTime GetDeadlineUtc(string tenantId, string ckTypeId, string methodId) =>
        CkMethodDeadline.FromTimeout(timeProvider.GetUtcNow().UtcDateTime, GetTimeout(tenantId, ckTypeId, methodId),
            CkMethodDeadline.MaxOf(options.Value));
}
