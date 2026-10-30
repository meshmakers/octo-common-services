using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Metrics;
using System.Diagnostics;
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
    CancellationToken CancellationToken)
{
    /// <summary>
    ///     The facts of this invocation that the filters share (AB#7816). One instance per call, created by the pipeline;
    ///     <c>context with { Request = ... }</c> keeps the same instance, so an outer filter reads after <c>next()</c> what
    ///     an inner one set. A context a test builds by hand starts with its own empty facts.
    /// </summary>
    public CkMethodInvocationFacts Facts { get; init; } = new();
}

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
    ILogger<CkMethodInvocationPipeline> logger,
    ICkMethodParametersFingerprinter? fingerprinter = null) : ICkMethodInvocationPipeline
{
    private readonly ICkMethodInvocationFilter[] _filters = filters.OrderBy(f => f.Order).ToArray();

    /// <summary>
    ///     Counts and times the whole call (AB#6920): around the filters and the transport, so every outcome of every
    ///     transport is seen once - also a refusal of the authorization filter, a replay and a call without any filter. It is
    ///     deliberately not a filter: the bands 100/200/300 stay the only ordering contract.
    /// </summary>
    public async Task<CkMethodResponse> ExecuteAsync(string transportName, CkMethodRequest request,
        CkMethodInvocationDelegate transport, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);

        var started = Stopwatch.GetTimestamp();
        // The span of the whole call; the bus message and the in-process handler span become its children, so the handler
        // side of a call is one trace with the gateway that made it. Nobody listening: no span, the trace continues.
        using var span = CkMethodTelemetry.StartSpan(CkMethodTelemetry.CallSpanName, ActivityKind.Client, request.MethodId,
            transportName);
        CkMethodResponse? response = null;
        var outcome = CkMethodErrorCodes.MethodHandlerFailed;
        try
        {
            response = await ExecuteCoreAsync(transportName, request, transport, cancellationToken).ConfigureAwait(false);
            outcome = CkMethodAuditRecord.OutcomeOf(response);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CkMethodAuditOutcomes.CallerCancelled;
            throw;
        }
        finally
        {
            span?.SetTag(CkMethodTelemetry.OutcomeTag, outcome);
            CkMethodMetrics.RecordFinished(request.MethodId, transportName, outcome, Stopwatch.GetElapsedTime(started),
                response);
        }
    }

    private async Task<CkMethodResponse> ExecuteCoreAsync(string transportName, CkMethodRequest request,
        CkMethodInvocationDelegate transport, CancellationToken cancellationToken)
    {
        // A call without a key costs nothing here; a keyed call needs the sensitivity and the fingerprint even when no
        // filter is registered, so that the missing-key refusal does not depend on which other filters exist.
        var keyed = fingerprinter != null && !string.IsNullOrEmpty(request.IdempotencyKey);
        if (_filters.Length == 0 && !keyed)
        {
            return await transport(request).ConfigureAwait(false);
        }

        var sensitive = sensitivity.GetSensitiveParameters(request.TenantId, request.CkTypeId, request.MethodId);
        var resultSensitive = sensitivity.IsResultSensitive(request.TenantId, request.CkTypeId, request.MethodId);
        var facts = new CkMethodInvocationFacts();
        var activeFilters = _filters;
        // Before redaction, from the raw values, and only for a call that carries a key (AB#7819). The fingerprinter is
        // registered together with the idempotency options, so the feature off means no cost and no refusal.
        if (keyed)
        {
            var result = fingerprinter!.Compute(request, sensitive is null || sensitive.Count > 0);
            facts.ParametersFingerprint = result.Primary;
            facts.AlternateParametersFingerprints = result.Alternates;
            if (result.KeyMissing)
            {
                activeFilters = [.. _filters.Append(new FingerprintKeyGuard(logger)).OrderBy(f => f.Order)];
            }
        }

        var view = CkMethodRequestRedaction.CreateView(request, sensitive);
        // The response mirrors the request side: the filters only ever see a redacted view (AB#7007), the raw response is
        // kept here and goes to the caller.
        CkMethodResponse? rawResponse = null;
        var context = new CkMethodInvocationContext(transportName, view, cancellationToken) { Facts = facts };
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

        for (var i = activeFilters.Length - 1; i >= 0; i--)
        {
            var filter = activeFilters[i];
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

/// <summary>
///     Refuses a keyed call to a method with sensitive parameters when no fingerprint key is configured (OD-4 G1): the
///     handler does not run, the idempotency filter behind it never sees the call. Carries no request value.
/// </summary>
internal sealed class FingerprintKeyGuard(ILogger logger) : ICkMethodInvocationFilter
{
    /// <summary>Between authorization and idempotency: an unauthorized caller learns nothing, the idempotency filter never sees the call.</summary>
    public int Order => (CkMethodInvocationFilterOrder.Authorization + CkMethodInvocationFilterOrder.Idempotency) / 2;

    public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} refused: an idempotency key was sent for a method with sensitive parameters, but no fingerprint key is configured ({Section})",
            context.Request.MethodId, context.Request.InvocationId, CkMethodFingerprintOptions.SectionName);
        return Task.FromResult(CkMethodResponse.Failed(context.Request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodValidationFailed,
                (CkMethodErrorParameters.MethodId, context.Request.MethodId),
                (CkMethodErrorParameters.Field, CkMethodIdempotencyRefusal.Field),
                (CkMethodErrorParameters.Reason, CkMethodIdempotencyRefusal.FingerprintKeyMissing))));
    }
}
