using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     <see cref="ICkMethodDispatcher" />: resolves the declaring element, prefers the in-process transport when the
///     method is hosted in this process and otherwise supplies the bus <c>send</c> as the terminal delegate of
///     <see cref="ICkMethodInvocationPipeline" />. Filters (audit, authorization, idempotency) are the pipeline's job.
///     The bus send happens only when the handler registry knows a live handler for the method (AB#8634); otherwise the
///     call is refused as <c>METHOD_HANDLER_NOT_AVAILABLE</c> / <c>no-live-handler</c> without touching the broker.
///     Right after the last handler of a method went away the call first waits a bounded hand-over time for a new
///     announcement (<see cref="CkMethodDispatcherOptions.HandlerHandOver" />), and a send the broker returned asks every
///     handler to announce itself again (<see cref="CkMethodHandlerDiscoveryRequester" />).
/// </summary>
internal sealed class CkMethodDispatcher(
    ICkMethodDeclaringElementResolver resolver,
    ICkMethodInvocationPipeline pipeline,
    CkMethodHandlerRegistry registry,
    IDistributionEventHubService eventHub,
    IServiceProvider serviceProvider,
    ICkMethodSensitivityResolver sensitivity,
    IOptions<CkMethodDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<CkMethodDispatcher> logger,
    CkMethodHandlerDiscoveryRequester? discovery = null) : ICkMethodDispatcher
{
    internal const string ReasonNoLiveHandler = "no-live-handler";
    internal const string ReasonTenantNotServed = "tenant-not-served";

    public async Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = timeProvider.GetTimestamp();
        // Which transport the call is on, and whether it entered the pipeline: the pipeline counts every call it runs
        // (also one whose exception escapes it) and the band-100 audit filter records the calls that reach the filters.
        // Only what happens before that is the dispatcher's to count and record (AB#7930).
        var transport = CkMethodTransportNames.Bus;
        var pipelineEntered = false;
        try
        {
            var methodId = resolver.Resolve(request);
            if (!string.Equals(methodId, request.MethodId, StringComparison.Ordinal))
            {
                request = request with { MethodId = methodId };
            }

            // Clamp first, before any transport, filter or send: from here on nothing can overflow for an absurd
            // deadline (DateTime.MaxValue) and the in-process and the bus path see the same request.
            request = NormalizeTimeout(request);

            // A method with a handler in this process (in-process, local only, or a bus handler this service hosts,
            // AB#8411) is found before any registry lookup; the local transport runs the same pipeline itself (transport
            // name "in-process") and nothing goes to the broker.
            var inProcess = serviceProvider.GetService(typeof(ICkInProcessMethodTransport)) as ICkInProcessMethodTransport;
            if (inProcess != null && inProcess.CanHandle(request.MethodId))
            {
                transport = CkMethodTransportNames.InProcess;
                pipelineEntered = true;
                return await inProcess.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }

            pipelineEntered = true;
            return await pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request,
                r => SendAsync(r, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. Inside the pipeline the audit filter records it; a cancellation that surfaces before the
            // pipeline (a resolver that observes the token) has no other observer than this line.
            if (!pipelineEntered)
            {
                await AuditAsync(request, transport, CkMethodAuditOutcomes.CallerCancelled, started, countCall: true,
                    cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception e)
        {
            // The contract is "never throws for a failing call"; the pipeline and the transports already answer their own
            // failures, this is the last line for the rest (resolver, DI, and what escapes the pipeline before its filters).
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(SafeRedacted(request, e), "CK method {MethodId} invocation {InvocationId} failed in the dispatcher (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            // AB#7930: the answer is a response, so the gateway (which records only exceptions it catches) does not see a
            // fault. An exception that escapes the pipeline was counted by the pipeline but no filter ran, so it needs the
            // event only; a fault before the pipeline needs the event and the count.
            await AuditAsync(request, transport, CkMethodErrorCodes.MethodHandlerFailed, started, countCall: !pipelineEntered,
                cancellationToken).ConfigureAwait(false);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>
    ///     Records the one audit event of a call that ended in the dispatcher before the filters of the pipeline could see it
    ///     (and counts it when the pipeline did not). Never throws: a failing recorder must not change the answer, and the
    ///     event is written with a token that is not cancelled, like the audit filter does. Without
    ///     <c>AddCkMethodAudit</c> there is no recorder and nothing is recorded.
    /// </summary>
    private async Task AuditAsync(CkMethodRequest request, string transport, string outcome, long started, bool countCall,
        CancellationToken cancellationToken)
    {
        var duration = timeProvider.GetElapsedTime(started);
        if (countCall)
        {
            CkMethodMetrics.RecordCall(request.MethodId, transport, outcome, duration);
        }

        try
        {
            if (serviceProvider.GetService(typeof(ICkMethodAuditRecorder)) is not ICkMethodAuditRecorder recorder)
            {
                return;
            }

            var subject = TrustedSubject(request, transport, cancellationToken);
            await recorder.RecordAsync(CkMethodAuditRecord.For(request, outcome, transport, duration, subject),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (CkMethodAuditFailedException)
        {
            // logged and counted by the recorder; the call is answered as failed anyway
        }
        catch (Exception e)
        {
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit event of the dispatcher could not be recorded",
                request.MethodId, request.InvocationId);
        }
    }

    /// <summary>The trusted caller like the audit filter reads it; <c>null</c> falls back to <c>request.Caller</c>.</summary>
    private string? TrustedSubject(CkMethodRequest request, string transport, CancellationToken cancellationToken)
    {
        try
        {
            if (serviceProvider.GetService(typeof(ICkMethodPrincipalProvider)) is not ICkMethodPrincipalProvider provider)
            {
                return null;
            }

            // a provider sees the redacted view only, like in the pipeline (AB#6914)
            var view = CkMethodRequestRedaction.CreateView(request,
                sensitivity.GetSensitiveParameters(request.TenantId, request.CkTypeId, request.MethodId));
            return provider.GetPrincipal(new CkMethodInvocationContext(transport, view, cancellationToken))?.Subject;
        }
        catch (Exception e)
        {
            // the type only: the text of a provider's exception may quote what it was given
            logger.LogWarning("CK method {MethodId}: the caller for the audit event could not be determined ({ExceptionType})",
                request.MethodId, e.GetType().Name);
            return null;
        }
    }

    /// <summary>
    ///     The deadline is at most now + <see cref="CkMethodDispatcherOptions.MaxTimeout" /> (also the deadline the handler
    ///     sees); <see cref="CkMethodRequest.TimeoutSeconds" /> is filled from the deadline when the caller left it out.
    /// </summary>
    private CkMethodRequest NormalizeTimeout(CkMethodRequest request)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var max = CkMethodDeadline.MaxOf(options.Value);
        var deadline = CkMethodDeadline.Clamp(request.DeadlineUtc, now, max);
        var seconds = request.TimeoutSeconds is { } given
            ? CkMethodDeadline.ClampSeconds(given)
            : CkMethodDeadline.ClampSeconds((int)Math.Ceiling(Math.Max((deadline - now).TotalSeconds, 0)));
        return deadline == request.DeadlineUtc && seconds == request.TimeoutSeconds
            ? request
            : request with { DeadlineUtc = deadline, TimeoutSeconds = seconds };
    }

    /// <summary>The bus transport: the terminal delegate of the pipeline.</summary>
    private async Task<CkMethodResponse> SendAsync(CkMethodRequest request, CancellationToken cancellationToken)
    {
        var sendStartedUtc = timeProvider.GetUtcNow().UtcDateTime;
        var opt = options.Value;
        var maxTimeout = CkMethodDeadline.MaxOf(opt);
        var grace = opt.ResponseGrace > TimeSpan.Zero ? opt.ResponseGrace : TimeSpan.Zero;

        // Clamp first: nothing below can overflow or throw for an absurd deadline (DateTime.MaxValue, TimeSpan.MaxValue).
        var remaining = request.DeadlineUtc - sendStartedUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return Timeout(request);
        }

        if (remaining > maxTimeout)
        {
            remaining = maxTimeout;
            request = request with { DeadlineUtc = sendStartedUtc + maxTimeout };
        }

        var live = registry.GetLiveHandlers(request.MethodId);
        if (live.Count == 0 && opt.RefuseWithoutLiveHandler)
        {
            // AB#8634: the request carries the parameters (passwords) and the caller's token. Without a live handler it is
            // refused here, before anything reaches the broker; a mandatory send that the broker returns has already
            // carried it there. Right after a start the registry may still fill, and right after the last handler went
            // away a new one may take over, so wait for the warm-up or the hand-over first.
            live = await WaitForLiveHandlerAsync(request, cancellationToken).ConfigureAwait(false);
            if (live.Count == 0)
            {
                return NotAvailable(request, ReasonNoLiveHandler);
            }

            // the wait used up part of the deadline
            remaining = request.DeadlineUtc - timeProvider.GetUtcNow().UtcDateTime;
            if (remaining <= TimeSpan.Zero)
            {
                return Timeout(request);
            }
        }

        if (live.Count > 0)
        {
            var serving = live.Where(e => CkMethodHandlerRegistry.Serves(e, request.TenantId)).ToList();
            if (serving.Count == 0)
            {
                return NotAvailable(request, ReasonTenantNotServed);
            }

            // Wire contract rule 4: never send a newer contract version than the handler announced.
            var supported = serving.Max(e => e.ContractVersion);
            if (request.ContractVersion > supported)
            {
                return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                    CkMethodError.Create(CkMethodErrorCodes.MethodContractVersionUnsupported,
                        (CkMethodErrorParameters.RequestedVersion, request.ContractVersion.ToString()),
                        (CkMethodErrorParameters.SupportedVersion, supported.ToString())));
            }
        }

        // The send stays mandatory: a live entry can be stale (handler crashed inside its TTL); the broker then returns the
        // request, the entry is expired and the next call is refused before the send. With RefuseWithoutLiveHandler=false
        // (emergency switch) an empty registry does not refuse and the broker decides, as before AB#8634.
        var wait = remaining + grace;
        // after a warm-up wait this is later than sendStartedUtc: an instance announced during the wait and returned by
        // the broker must be expired too
        var sentAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            return await eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(
                CkMethodQueueNames.For(request.MethodId), request,
                new CommandSendOptions
                {
                    Timeout = wait,
                    Mandatory = true,
                    TimeToLive = wait,
                    // parameters and the caller token must not reach the broker's persistent store
                    Persistent = false
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CommandNotRoutableException)
        {
            // The registry listed a handler, the broker had no queue for it: a handler may be re-binding its queue (broker
            // reconnect). Live handlers answer the discovery request at once, so the next calls (which wait for the
            // hand-over) find them instead of being refused until the next heartbeat. This call is not sent again. A
            // repeated return (the handler answered but its queue is still not bound) does not ask again.
            if (registry.MarkNoQueueBound(request.MethodId, sentAtUtc))
            {
                discovery?.Request();
            }
            return NotAvailable(request, ReasonNoLiveHandler);
        }
        catch (DistributionTimeoutException)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: no answer within {Timeout} (outcome unknown)",
                request.MethodId, request.InvocationId, request.TenantId, wait);
            return Timeout(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is DistributedOperationFailedException or OperationCanceledException)
        {
            // the bus is stopping or the send failed: a platform outcome, not an exception for the caller
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(Redactor(request).Redact(e),
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: bus transport failed (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, request.TenantId, correlationId);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>
    ///     Waits for a live handler of the method (at most until the window below ends or the request deadline passes) and
    ///     returns the live handlers, or an empty list when none announced itself in time. The window is
    ///     <list type="bullet">
    ///         <item>the registry warm-up while the registry has not heard of the method yet or knows it only from the
    ///         restored snapshot;</item>
    ///         <item>otherwise the hand-over: <see cref="CkMethodDispatcherOptions.HandlerHandOver" /> from the moment the
    ///         last entry of the method ended (withdrawal, expiry, returned send). A call after it is refused at once.</item>
    ///     </list>
    ///     The wait holds no thread and follows the caller's token; hand-over waits are limited to
    ///     <see cref="CkMethodDispatcherOptions.MaxWaitingCalls" /> calls at a time, a call beyond it is refused at once.
    /// </summary>
    private async Task<IReadOnlyList<CkMethodHandlerEntry>> WaitForLiveHandlerAsync(CkMethodRequest request,
        CancellationToken cancellationToken)
    {
        // Admission to the bounded hand-over is taken when the wait is (or becomes) a hand-over; the warm-up wait after a
        // start is not counted. A warm-up wait that turns into a hand-over (a withdrawal arrived) is admitted then.
        var admitted = false;
        try
        {
            while (true)
            {
                // take the signal before the check, so an announcement between the check and the wait is not missed
                var changed = registry.WaitForChangeAsync();
                var live = registry.GetLiveHandlers(request.MethodId);
                if (live.Count > 0)
                {
                    return live;
                }

                var now = timeProvider.GetUtcNow().UtcDateTime;
                var (until, askingForDiscovery, handOver) = WaitUntil(request);
                if (until <= now)
                {
                    return [];
                }

                if (handOver && !admitted)
                {
                    if (!registry.TryEnterWait(options.Value.MaxWaitingCalls))
                    {
                        logger.LogWarning("CK method {MethodId}: too many calls wait for a handler, the call is refused at once",
                            request.MethodId);
                        return [];
                    }

                    admitted = true;
                }

                // while the handlers are asked to announce themselves, wake up to ask again (the request is throttled)
                var step = until - now;
                if (askingForDiscovery && step > CkMethodHandlerDiscoveryRequester.MinInterval)
                {
                    step = CkMethodHandlerDiscoveryRequester.MinInterval;
                }

                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(step, timeProvider, delayCancellation.Token);
                await Task.WhenAny(changed, delay).ConfigureAwait(false);
                await delayCancellation.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (admitted)
            {
                registry.LeaveWait();
            }
        }
    }

    /// <summary>
    ///     End of the wait for a live handler (see <see cref="WaitForLiveHandlerAsync" />), never after the request deadline.
    ///     A method whose last entry ended because the broker returned a send asks the handlers to announce themselves
    ///     again while its hand-over runs (at most one request per second, see <see cref="CkMethodHandlerDiscoveryRequester" />):
    ///     a handler that was still re-binding when the first request went out answers a later one.
    ///     <c>AskingForDiscovery</c> is <c>true</c> while that runs. A return that repeated an earlier one (the handler
    ///     answered but its queue is still not bound) gets neither a wait nor a request: its calls are refused at once until
    ///     the handler's next announcement, as before the hand-over existed. <c>HandOver</c> is <c>true</c> for the
    ///     hand-over window, <c>false</c> for the warm-up.
    /// </summary>
    private (DateTime Until, bool AskingForDiscovery, bool HandOver) WaitUntil(CkMethodRequest request)
    {
        // An end this registry observed itself (withdrawal, expiry or returned send of an entry it received) decides;
        // only a method it has not heard of, or knows only from the restored snapshot, gets the warm-up.
        var ended = registry.GetLastEnded(request.MethodId);
        if (ended == null)
        {
            return (Earliest(registry.WarmUpEndsUtc, request.DeadlineUtc), false, false);
        }

        var handOver = HandOverOf(options.Value);
        if (handOver <= TimeSpan.Zero || ended.Value.RepeatedNoQueueBound)
        {
            return (DateTime.MinValue, false, true);
        }

        var until = ended.Value.EndedUtc + handOver;
        var asking = false;
        if (discovery != null && ended.Value.NoQueueBound && until > timeProvider.GetUtcNow().UtcDateTime)
        {
            discovery.Request();
            asking = true;
        }

        return (Earliest(until, request.DeadlineUtc), asking, true);
    }

    private static DateTime Earliest(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>The configured hand-over, at most <see cref="CkMethodDispatcherOptions.MaxHandlerHandOver" />.</summary>
    internal static TimeSpan HandOverOf(CkMethodDispatcherOptions options) =>
        options.HandlerHandOver > CkMethodDispatcherOptions.MaxHandlerHandOver
            ? CkMethodDispatcherOptions.MaxHandlerHandOver
            : options.HandlerHandOver;

    /// <summary>Masks the token and the sensitive parameters in exceptions of the bus or the resolver (AB#6914).</summary>
    private CkMethodRedactor Redactor(CkMethodRequest request) =>
        CkMethodRedactor.For(sensitivity.GetSensitiveParameters(request.TenantId, request.CkTypeId, request.MethodId),
            request);

    /// <summary>
    ///     <see cref="Redactor" /> for the last line: a fault of the sensitivity lookup itself must not escape the dispatcher
    ///     (nothing of the exception is logged then, it may quote the payload).
    /// </summary>
    private Exception? SafeRedacted(CkMethodRequest request, Exception e)
    {
        try
        {
            return Redactor(request).Redact(e);
        }
        catch
        {
            return null;
        }
    }

    private CkMethodResponse NotAvailable(CkMethodRequest request, string reason)
    {
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: no handler available ({Reason})",
            request.MethodId, request.InvocationId, request.TenantId, reason);
        return CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.Reason, reason)));
    }

    private static CkMethodResponse Timeout(CkMethodRequest request) =>
        CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodTimeout,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.TimeoutSeconds,
                    CkMethodDeadline.ClampSeconds(request.TimeoutSeconds ?? CkMethodExecutionDto.DefaultTimeoutSeconds)
                        .ToString())));

    private static CkMethodResponse HandlerFailed(CkMethodRequest request, Guid correlationId) =>
        CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
}
