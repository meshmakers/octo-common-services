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
///     Order bands of the pipeline filters. Lower runs first (outermost). The order is part of the contract: audit is
///     outermost so that it sees every call and its outcome, authorization comes next, idempotency is innermost, directly
///     before the transport. Filters of other features pick a value inside their band.
/// </summary>
public static class CkMethodInvocationFilterOrder
{
    /// <summary>
    ///     Audit (F3.5): outermost. Records every call and its final outcome, including refusals by the authorization
    ///     filter and replays answered by the idempotency filter (exactly one audit event per invocation).
    /// </summary>
    public const int Audit = 100;

    /// <summary>Authorization (F3.4): may refuse the call; nothing inside it (idempotency, transport) runs then.</summary>
    public const int Authorization = 200;

    /// <summary>
    ///     Idempotency (F3.5): innermost before the transport. A stored outcome is replayed only after the caller was
    ///     authorized.
    /// </summary>
    public const int Idempotency = 300;
}

/// <summary>The inner part of the pipeline: the next filter or, at the end, the transport.</summary>
/// <param name="request">The request to continue with. A filter may pass a modified copy (<c>request with { ... }</c>).</param>
public delegate Task<CkMethodResponse> CkMethodInvocationDelegate(CkMethodRequest request);

/// <summary>What a filter knows about the call it wraps.</summary>
/// <param name="Transport">One of <see cref="CkMethodTransportNames" />.</param>
/// <param name="Request">
///     A <b>redacted view</b> of the request (AB#6914): the values of the parameters marked <c>sensitive</c> in the CK
///     method are replaced by <c>"***"</c> (all values when the method is not known), <c>CallerAccessToken</c> is
///     <c>null</c>. Everything else is as the caller built it. The raw request goes only to the transport; a filter that
///     passes this request (or a copy of it) to <c>next</c> continues with the raw values; a filter that edits
///     <c>ParametersJson</c> keeps the raw value of every sensitive member it leaves at the placeholder (AB#7016).
/// </param>
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
    ICkMethodSensitivityResolver sensitivity,
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

        var sensitive = sensitivity.GetSensitiveParameters(request.TenantId, request.CkTypeId, request.MethodId);
        var resultSensitive = sensitivity.IsResultSensitive(request.TenantId, request.CkTypeId, request.MethodId);
        var view = CkMethodRequestRedaction.CreateView(request, sensitive);
        // The response mirrors the request side: the filters only ever see a redacted view (AB#7007), the raw response is
        // kept here and goes to the caller.
        CkMethodResponse? rawResponse = null;
        var context = new CkMethodInvocationContext(transportName, view, cancellationToken);
        // The filters only ever see the redacted view; the terminal step puts the raw values back for the transport.
        CkMethodInvocationDelegate chain = r =>
        {
            if (CkMethodRequestRedaction.TryRestore(request, view, r, sensitive, out var restored, out var member))
            {
                return TransportAsync(restored);
            }

            // a filter handed on the placeholder for a sensitive member the request does not carry: never call the
            // handler with "***" as a value (AB#7016)
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} refused: an invocation filter returned the redacted placeholder for sensitive parameter '{Field}' that the request does not carry",
                request.MethodId, request.InvocationId, member);
            return Task.FromResult(CkMethodResponse.Failed(request.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodValidationFailed,
                    (CkMethodErrorParameters.MethodId, request.MethodId),
                    (CkMethodErrorParameters.Field, member ?? string.Empty),
                    (CkMethodErrorParameters.Reason, "redacted placeholder without a value in the request"))));
        };
        async Task<CkMethodResponse> TransportAsync(CkMethodRequest r)
        {
            var raw = await transport(r).ConfigureAwait(false);
            rawResponse = raw;
            return CkMethodResponseRedaction.CreateView(raw, resultSensitive);
        }

        for (var i = _filters.Length - 1; i >= 0; i--)
        {
            var filter = _filters[i];
            var next = chain;
            chain = r => filter.InvokeAsync(context with { Request = r }, next);
        }

        try
        {
            var passed = await chain(view).ConfigureAwait(false);
            return CkMethodResponseRedaction.Restore(rawResponse, passed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(CkMethodRedactor.For(sensitive, request).Redact(e),
                "CK method {MethodId} invocation {InvocationId} failed in an invocation filter (transport {Transport}, correlation {CorrelationId})",
                request.MethodId, request.InvocationId, transportName, correlationId);
            return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                    (CkMethodErrorParameters.MethodId, request.MethodId),
                    (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
        }
    }
}
