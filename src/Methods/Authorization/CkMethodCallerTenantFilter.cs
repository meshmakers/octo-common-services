using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     Refuses every <b>in-process</b> CK method call whose caller token was issued for another tenant than the call addresses
///     (AB#8711): <c>METHOD_FORBIDDEN</c> with the reason <see cref="CkMethodTokenReasons.TenantNotAllowed" />, before the
///     declared authorization, the idempotency store and the handler. It applies the same rule as the bus host
///     (<see cref="ICkMethodCallerTokenValidator" />, <see cref="CkMethodCallerTenantRule" />): the <c>tenant_id</c> claim must
///     name the call's tenant; a service token (no <c>sub</c>) may also be on <c>TenantAuthorization:CrossTenantServiceClientIds</c>.
/// </summary>
/// <remarks>
///     <para>
///         Why here: a bus-hosted handler re-validates the forwarded token itself; an in-process handler (a method the gateway
///         owns, or a bus handler the service calls itself) has no such host and relied on the HTTP
///         <c>TenantAuthorizationMiddleware</c> alone, which may only log (<c>UserTokenEnforcement=LogOnly</c>). This filter does
///         not read the middleware's modes: it always enforces.
///     </para>
///     <para>
///         Registered with the local transport (<c>AddCkInProcessMethodHandlers</c>, <c>AddCkMethodHandlers</c>), not optional.
///         Band <see cref="CkMethodInvocationFilterOrder.CallerTenant" />. On the bus transport it does nothing (the bus host
///         judges the token). A principal that was not mapped from an access token (<see cref="CkMethodPrincipal.IsFromAccessToken" />
///         false: the identity a service built for its own job) has no token tenant and is not judged here; a missing principal
///         is left to the authorization filter, which refuses it.
///     </para>
///     <para>The log names the tenant of the call, the method and a fixed category; never a token, a claim value or a parameter.</para>
/// </remarks>
public sealed class CkMethodCallerTenantFilter(
    IOptions<TenantAuthorizationOptions> tenantOptions,
    ILogger<CkMethodCallerTenantFilter> logger,
    ICkMethodPrincipalProvider? principalProvider = null) : ICkMethodInvocationFilter
{
    /// <inheritdoc />
    public int Order => CkMethodInvocationFilterOrder.CallerTenant;

    /// <inheritdoc />
    public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Request;
        if (!string.Equals(context.Transport, CkMethodTransportNames.InProcess, StringComparison.Ordinal))
        {
            return next(request);
        }

        var principal = (principalProvider ?? CkMethodPrincipalScope.Instance).GetPrincipal(context);
        if (principal is not { IsFromAccessToken: true })
        {
            return next(request);
        }

        var verdict = CkMethodCallerTenantRule.Evaluate(principal.SubjectId, principal.ClientId, principal.TokenTenantId,
            request.TenantId, tenantOptions.Value);
        if (verdict == CkMethodCallerTenantVerdict.Allowed)
        {
            return next(request);
        }

        var reason = verdict == CkMethodCallerTenantVerdict.NoIdentity
            ? CkMethodTokenReasons.TokenInvalid
            : CkMethodTokenReasons.TenantNotAllowed;
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} refused in tenant {TenantId} (transport {Transport}): caller token {Reason} ({Verdict})",
            request.MethodId, request.InvocationId, request.TenantId, context.Transport, reason, verdict);
        return Task.FromResult(CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.Reason, reason))));
    }
}
