using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     Enforces the <c>authorization</c> a model author declared on a CK method (AB#6911, F3.4) for every call, over the
///     bus and in-process alike. Register it with <c>AddCkMethodAuthorization()</c> (or
///     <c>AddCkMethodInvocationFilter&lt;CkMethodAuthorizationFilter&gt;()</c> plus the services it needs).
/// </summary>
/// <remarks>
///     Band <see cref="CkMethodInvocationFilterOrder.Authorization" />: inside the audit filter, which therefore records a
///     refusal, and outside the idempotency filter and the transport, which a refused call never reaches. The caller comes
///     from <see cref="ICkMethodPrincipalProvider" />, never from <c>CkMethodRequest.Caller</c> or the forwarded token of
///     a remote message. A refusal is a normal failed response <c>METHOD_FORBIDDEN</c> with a fixed reason
///     (<see cref="CkMethodAuthorizationErrors" />); the filter does not look at the target entity, so the answer is the
///     same whether it exists or not. It is the second line behind the gateway gate and the first for callers that do not
///     pass the gateway (REST, jobs, pipelines).
/// </remarks>
public sealed class CkMethodAuthorizationFilter(
    ICkMethodPrincipalProvider principalProvider,
    ICkMethodAuthorizationEvaluator evaluator,
    ICkMethodAuthorizationDefinitionSource definitionSource,
    ILogger<CkMethodAuthorizationFilter> logger) : ICkMethodInvocationFilter
{
    /// <inheritdoc />
    public int Order => CkMethodInvocationFilterOrder.Authorization;

    /// <inheritdoc />
    public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Request;
        var definition = definitionSource.Find(request.TenantId, request.CkTypeId, request.MethodId);
        var decision = definition == null
            ? CkMethodAuthorizationDecision.Deny(CkMethodDenial.UnknownMethod)
            : evaluator.Evaluate(definition.Authorization, definition.Kind, request.TargetRtId,
                principalProvider.GetPrincipal(context));

        if (decision.Allowed)
        {
            return next(request);
        }

        // The log names the rule, the response does not. No parameters, token or target id are logged.
        logger.LogInformation(
            "CK method {MethodId} invocation {InvocationId} refused in tenant {TenantId} (transport {Transport}): {Denial}",
            request.MethodId, request.InvocationId, request.TenantId, context.Transport, decision.Denial);
        return Task.FromResult(CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodAuthorizationErrors.Forbidden(request.MethodId)));
    }
}
