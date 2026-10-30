using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     The bus transport of the handler host: consumes <see cref="CkMethodRequest" /> from the method queue, guards
///     against a possible duplicate delivery (broker redelivery or retry, then a repeated invocation id) and answers with the response of <see cref="ICkMethodHandlerInvoker" />.
///     It never throws: a thrown consumer would make the bus retry (AB#4348) and run the method repeatedly.
/// </summary>
internal sealed class CkMethodBusConsumer(
    ICkMethodHandlerInvoker invoker,
    CkMethodHandlerInstance instance,
    TimeProvider timeProvider,
    ILogger<CkMethodBusConsumer> logger)
{
    private readonly CkMethodSeenInvocations _seen = new(timeProvider);

    internal int SeenInvocationCount => _seen.Count;

    public ExecuteDeliveredCommandHandler<CkMethodRequest> CreateHandler()
    {
        return async (request, delivery, respond) =>
        {
            var response = await HandleAsync(request, delivery).ConfigureAwait(false);
            try
            {
                await respond(response).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Swallowed on purpose: rethrowing would let the bus retry re-execute the method. The caller
                // runs into METHOD_TIMEOUT instead (at-most-once).
                // the exception can quote the response: a sensitive (or unknown) result is masked in it (AB#7007)
                var logged = response.ResultJson is not null && IsResultSensitive(request.MethodId)
                    ? CkMethodRedactor.ForToken(request).WithResult(response.ResultJson).Redact(e)
                    : e;
                logger.LogError(logged,
                    "CK method {MethodId} invocation {InvocationId}: sending the response failed",
                    request.MethodId, request.InvocationId);
            }
        };
    }

    private bool IsResultSensitive(string methodId) =>
        invoker is not CkMethodHandlerInvoker concrete || concrete.IsResultSensitive(methodId);

    /// <param name="request">The request.</param>
    /// <param name="delivery">
    ///     Transport delivery info. A possible duplicate (broker redelivery after a lost acknowledgement, bus retry) is
    ///     not executed: the previous delivery may have run the method, and no method is known to be idempotent, so the
    ///     caller gets <c>METHOD_OUTCOME_UNKNOWN</c>. <c>null</c> for transports without delivery info.
    /// </param>
    internal async Task<CkMethodResponse> HandleAsync(CkMethodRequest request, CommandDeliveryInfo? delivery = null)
    {
        // The decision (broker flag, then the invocation id as second line, clamped retention) is the shared helper's.
        switch (_seen.Decide(request, delivery))
        {
            case CkMethodDeliveryDecision.PossibleDuplicate:
                logger.LogWarning(
                    "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: possible duplicate delivery " +
                    "(redelivered {Redelivered}, retry attempt {RetryAttempt}, redelivery count {RedeliveryCount}, message {MessageId})",
                    request.MethodId, request.InvocationId, request.TenantId, delivery!.Redelivered, delivery.RetryAttempt,
                    delivery.RedeliveryCount, delivery.MessageId);
                return OutcomeUnknown(request);
            case CkMethodDeliveryDecision.AlreadySeen:
                logger.LogWarning(
                    "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: invocation id already seen",
                    request.MethodId, request.InvocationId, request.TenantId);
                return OutcomeUnknown(request);
            default:
                return await invoker.InvokeAsync(request).ConfigureAwait(false);
        }
    }

    private CkMethodResponse OutcomeUnknown(CkMethodRequest request) =>
        CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                (CkMethodErrorParameters.MethodId, request.MethodId)));
}
