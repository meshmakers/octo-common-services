using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>Names of the transports that run the invocation pipeline (<see cref="CkMethodInvocationContext.Transport" />).</summary>
public static class CkMethodTransportNames
{
    /// <summary>The handler runs in the process of the caller, no bus hop.</summary>
    public const string InProcess = "in-process";

    /// <summary>The request travels over the event hub to a remote handler.</summary>
    public const string Bus = "bus";
}

/// <summary>
///     Suggested <see cref="ICkMethodInvocationFilter.Order" /> bands. Lower runs first (outermost). Filters of other
///     features pick a value inside their band so that authorization always runs before audit and idempotency.
/// </summary>
public static class CkMethodInvocationFilterOrder
{
    /// <summary>Authorization (F3.4): may refuse the call before anything else happens.</summary>
    public const int Authorization = 100;

    /// <summary>Audit (F3.5): records the call and its outcome, including refusals of the authorization filter.</summary>
    public const int Audit = 200;

    /// <summary>Idempotency (F3.5): may answer a repeated call from a stored outcome without running the handler.</summary>
    public const int Idempotency = 300;
}

/// <summary>The inner part of the pipeline: the next filter or, at the end, the transport.</summary>
/// <param name="request">The request to continue with. A filter may pass a modified copy (<c>request with { ... }</c>).</param>
public delegate Task<CkMethodResponse> CkMethodInvocationDelegate(CkMethodRequest request);

/// <summary>What a filter knows about the call it wraps.</summary>
/// <param name="Transport">One of <see cref="CkMethodTransportNames" />.</param>
/// <param name="Request">The request as the caller built it. Sensitive: its <c>ToString()</c> is safe, its members are not.</param>
/// <param name="CancellationToken">Fires when the caller gives up (not at the deadline; the deadline is part of the request).</param>
public sealed record CkMethodInvocationContext(
    string Transport,
    CkMethodRequest Request,
    CancellationToken CancellationToken);

/// <summary>
///     One step around every method invocation, for the in-process and the bus transport alike. This is the extension
///     point for authorization (F3.4: <see cref="CkMethodInvocationFilterOrder.Authorization" />) and for audit and
///     idempotency (F3.5). A filter answers a refusal with a normal failed <see cref="CkMethodResponse" /> and does not
///     call <c>next</c>; it never lets an exception escape for a business outcome. An exception that does
///     escape is answered as <c>METHOD_HANDLER_FAILED</c>.
/// </summary>
public interface ICkMethodInvocationFilter
{
    /// <summary>Position in the pipeline; lower runs first. See <see cref="CkMethodInvocationFilterOrder" />.</summary>
    int Order { get; }

    /// <summary>Runs the step. Call the next delegate to continue the pipeline.</summary>
    Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next);
}

/// <summary>
///     Runs the registered <see cref="ICkMethodInvocationFilter" />s around a transport. Both transports use the same
///     implementation, so a filter sees every call regardless of where the handler runs. Resolve, deadline, invoke and
///     the response envelope are the steps inside the transport; the filters wrap them.
///     Like the invoker it never throws for a failing call: only cancellation by the caller surfaces.
/// </summary>
public interface ICkMethodInvocationPipeline
{
    /// <summary>Runs the filters and, at the end, <paramref name="transport" />.</summary>
    /// <param name="transportName">One of <see cref="CkMethodTransportNames" />.</param>
    /// <param name="request">The request.</param>
    /// <param name="transport">Sends the (possibly modified) request to the handler and returns the envelope.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<CkMethodResponse> ExecuteAsync(string transportName, CkMethodRequest request,
        CkMethodInvocationDelegate transport, CancellationToken cancellationToken = default);
}

internal sealed class CkMethodInvocationPipeline(
    IEnumerable<ICkMethodInvocationFilter> filters,
    ILogger<CkMethodInvocationPipeline> logger) : ICkMethodInvocationPipeline
{
    private readonly ICkMethodInvocationFilter[] _filters = filters.OrderBy(f => f.Order).ToArray();

    public async Task<CkMethodResponse> ExecuteAsync(string transportName, CkMethodRequest request,
        CkMethodInvocationDelegate transport, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);

        if (_filters.Length == 0)
        {
            return await transport(request).ConfigureAwait(false);
        }

        var context = new CkMethodInvocationContext(transportName, request, cancellationToken);
        var chain = transport;
        for (var i = _filters.Length - 1; i >= 0; i--)
        {
            var filter = _filters[i];
            var next = chain;
            chain = r => filter.InvokeAsync(context with { Request = r }, next);
        }

        try
        {
            return await chain(request).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(CkMethodRedactor.ForToken(request).Redact(e),
                "CK method {MethodId} invocation {InvocationId} failed in an invocation filter (transport {Transport}, correlation {CorrelationId})",
                request.MethodId, request.InvocationId, transportName, correlationId);
            return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                    (CkMethodErrorParameters.MethodId, request.MethodId),
                    (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
        }
    }
}
