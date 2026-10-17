using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     The bus transport of the handler host: consumes <see cref="CkMethodRequest" /> from the method queue, guards
///     against a repeated invocation id and answers with the response of <see cref="ICkMethodHandlerInvoker" />.
///     It never throws: a thrown consumer would make the bus retry (AB#4348) and run the method repeatedly.
/// </summary>
internal sealed class CkMethodBusConsumer(
    ICkMethodHandlerInvoker invoker,
    CkMethodHandlerInstance instance,
    TimeProvider timeProvider,
    ILogger<CkMethodBusConsumer> logger)
{
    private static readonly TimeSpan SeenRetentionAfterDeadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSeenRetention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<Guid, DateTime> _seenInvocations = new();
    private long _seenAdds;

    internal int SeenInvocationCount => _seenInvocations.Count;

    public ExecuteCommandHandler<CkMethodRequest> CreateHandler()
    {
        return async (request, respond) =>
        {
            var response = await HandleAsync(request).ConfigureAwait(false);
            try
            {
                await respond(response).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Swallowed on purpose: rethrowing would let the bus retry re-execute the method. The caller
                // runs into METHOD_TIMEOUT instead (at-most-once).
                logger.LogError(e,
                    "CK method {MethodId} invocation {InvocationId}: sending the response failed",
                    request.MethodId, request.InvocationId);
            }
        };
    }

    internal async Task<CkMethodResponse> HandleAsync(CkMethodRequest request)
    {
        // The broker's redelivery flag is not visible to consumers of the current event hub (no delivery info), so
        // duplicate delivery is detected by the invocation id alone, per handler instance.
        if (!TryMarkSeen(request))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: invocation id already seen",
                request.MethodId, request.InvocationId, request.TenantId);
            return OutcomeUnknown(request);
        }

        return await invoker.InvokeAsync(request).ConfigureAwait(false);
    }

    private CkMethodResponse OutcomeUnknown(CkMethodRequest request) =>
        CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                (CkMethodErrorParameters.MethodId, request.MethodId)));

    private bool TryMarkSeen(CkMethodRequest request)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var keepUntil = request.DeadlineUtc + SeenRetentionAfterDeadline;
        if (keepUntil > now + MaxSeenRetention)
        {
            keepUntil = now + MaxSeenRetention;
        }

        if (!_seenInvocations.TryAdd(request.InvocationId, keepUntil))
        {
            return false;
        }

        if (Interlocked.Increment(ref _seenAdds) % 256 == 0)
        {
            foreach (var entry in _seenInvocations.Where(e => e.Value < now))
            {
                _seenInvocations.TryRemove(entry.Key, out _);
            }
        }

        return true;
    }
}
