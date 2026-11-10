using System.Collections.Concurrent;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     Marker of a host that runs accepted operations (<c>AddCkMethodOperations</c>: runner plus heartbeat). A host with
///     only the sweep has no heartbeat, so its handlers must not accept.
/// </summary>
internal sealed class CkMethodOperationHost(CkMethodOperationRunner runner)
{
    public CkMethodOperationRunner Runner { get; } = runner;
}

/// <summary>What the invoker hands over when a handler accepted a call (AB#8443).</summary>
/// <param name="Request">The request (only ids are kept from it; never the parameters or the token).</param>
/// <param name="Principal">The trusted principal of the call (owner of the operation), or <c>null</c>.</param>
/// <param name="SensitiveResult"><c>true</c> when the CK result is sensitive: the result is not stored.</param>
/// <param name="HandlerInstanceId">Instance id of this handler host.</param>
/// <param name="Scope">The handler's DI scope; the runner owns it from now on and disposes it when the work ends.</param>
/// <param name="Work">The accepted work.</param>
/// <param name="MapFailure">Maps a declared failure of the work to the wire error (undeclared code = <c>METHOD_HANDLER_FAILED</c>).</param>
/// <param name="Redact">Masks secrets in an exception of the work before it is logged.</param>
internal sealed record CkMethodOperationStart(
    CkMethodRequest Request,
    CkMethodPrincipal? Principal,
    bool SensitiveResult,
    string HandlerInstanceId,
    AsyncServiceScope Scope,
    Func<ICkMethodOperationProgress, CancellationToken, Task<CkMethodAdapterOutcome>> Work,
    Func<CkMethodFailure, CkMethodError> MapFailure,
    Func<Exception, Exception> Redact);

/// <summary>
///     Runs accepted operations of asynchronous CK methods (AB#8443, OD-5): writes the record <b>before</b> the accepted
///     envelope is returned, runs the work once in a detached flow, keeps the heartbeat of its running operations and
///     writes the final state. Lost operations of any host are ended by <see cref="SweepAsync" />. Nothing is ever
///     restarted (at most once after accept).
/// </summary>
internal sealed class CkMethodOperationRunner(
    ICkMethodOperationStore store,
    IOptions<CkMethodOperationOptions> options,
    TimeProvider timeProvider,
    ILogger<CkMethodOperationRunner> logger)
{
    // bound of one store write after the work ended (the host may be stopping)
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string TenantId, string OperationId), Task> _running = new();
    private readonly CancellationTokenSource _stopping = new();

    private CkMethodOperationOptions Options => options.Value;

    /// <summary>Number of operations this host is working on.</summary>
    public int RunningCount => _running.Count;

    /// <summary>
    ///     Records the operation and starts the work. Returns the accepted envelope only after the record is stored; when
    ///     the store fails, the call is answered <c>METHOD_HANDLER_NOT_AVAILABLE</c> (reason
    ///     <see cref="CkMethodOperationReasons.StoreUnavailable" />) and the work never starts. After <see cref="StopAsync" />
    ///     began, nothing is recorded and the answer is <c>METHOD_HANDLER_NOT_AVAILABLE</c> (reason
    ///     <see cref="CkMethodOperationReasons.HostStopping" />), so the caller can retry on another replica.
    /// </summary>
    public async Task<CkMethodResponse> AcceptAsync(CkMethodOperationStart start, CancellationToken cancellationToken)
    {
        var request = start.Request;
        if (_stopping.IsCancellationRequested)
        {
            // a stopping host must not hand out a handle whose work would end as lost right away
            await start.Scope.DisposeAsync().ConfigureAwait(false);
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT accepted: the host is stopping",
                request.MethodId, request.InvocationId, request.TenantId);
            return NotAvailable(start, CkMethodOperationReasons.HostStopping);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var record = new CkMethodOperationRecord
        {
            TenantId = CkMethodInMemoryOperationStore.Normalize(request.TenantId),
            OperationId = Guid.NewGuid().ToString("N"),
            MethodId = request.MethodId,
            InvocationId = request.InvocationId,
            CorrelationId = request.CorrelationId,
            Owner = CkMethodOperationOwner.Of(start.Principal),
            HandlerInstanceId = start.HandlerInstanceId,
            State = CkMethodOperationState.Accepted,
            AcceptedAtUtc = now,
            HeartbeatAtUtc = now
        };

        try
        {
            await store.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the caller gave up; a record that was written anyway is ended by the sweep
            await start.Scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception e)
        {
            await start.Scope.DisposeAsync().ConfigureAwait(false);
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT accepted: the operation record could not be written ({ExceptionType}); the work was not started",
                request.MethodId, request.InvocationId, request.TenantId, e.GetType().Name);
            return NotAvailable(start, CkMethodOperationReasons.StoreUnavailable);
        }

        CkMethodMetrics.RecordOperation(request.MethodId, CkMethodOperationState.Accepted);
        logger.LogInformation(
            "CK method {MethodId} invocation {InvocationId} tenant {TenantId} accepted as operation {OperationId}",
            request.MethodId, request.InvocationId, request.TenantId, record.OperationId);

        var key = (record.TenantId, record.OperationId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run;
        // Detached (OD-8): the work must not see the caller's ambient state (request session, principal scope).
        using (ExecutionContext.SuppressFlow())
        {
            run = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                await RunAsync(start, record).ConfigureAwait(false);
            });
        }

        _running[key] = run;
        _ = run.ContinueWith(_ => _running.TryRemove(key, out var _), TaskScheduler.Default);
        gate.SetResult();

        return CkMethodResponse.Accepted(request.InvocationId, start.HandlerInstanceId, new CkMethodOperationHandle
        {
            OperationId = record.OperationId,
            AcceptedAtUtc = record.AcceptedAtUtc,
            RetryAfterSeconds = Options.RetryAfterSeconds
        });
    }

    private static CkMethodResponse NotAvailable(CkMethodOperationStart start, string reason) =>
        CkMethodResponse.Failed(start.Request.InvocationId, start.HandlerInstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.MethodId, start.Request.MethodId),
                (CkMethodErrorParameters.Reason, reason)));

    private async Task RunAsync(CkMethodOperationStart start, CkMethodOperationRecord record)
    {
        var request = start.Request;
        var correlationId = request.CorrelationId ?? request.InvocationId;
        await using var scope = start.Scope;
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MethodId"] = request.MethodId,
            ["InvocationId"] = request.InvocationId,
            ["CorrelationId"] = correlationId,
            ["TenantId"] = request.TenantId,
            ["OperationId"] = record.OperationId
        });

        try
        {
            if (!await store.MarkRunningAsync(record.TenantId, record.OperationId,
                    timeProvider.GetUtcNow().UtcDateTime, _stopping.Token).ConfigureAwait(false))
            {
                // the sweep ended it already (the host was too slow to start); at most once: never run it now
                logger.LogWarning("CK method operation {OperationId} was ended before its work started; not run",
                    record.OperationId);
                return;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(
                "CK method operation {OperationId}: the start could not be recorded ({ExceptionType}); not run",
                record.OperationId, e.GetType().Name);
            await CompleteAsync(record, Lost(record.MethodId)).ConfigureAwait(false);
            return;
        }

        CkMethodMetrics.RecordOperation(record.MethodId, CkMethodOperationState.Running);
        CkMethodOperationCompletion completion;
        try
        {
            var outcome = await start.Work(new Reporter(store, timeProvider, logger, record), _stopping.Token).ConfigureAwait(false);
            completion = outcome.Failure is { } failure
                ? Failed(start.MapFailure(failure))
                : Succeeded(start.SensitiveResult ? null : outcome.ResultJson);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            logger.LogWarning("CK method operation {OperationId} stopped with the host; outcome unknown",
                record.OperationId);
            completion = Lost(record.MethodId);
        }
        catch (CkMethodException e)
        {
            completion = Failed(start.MapFailure(e.Failure));
        }
        catch (Exception e) when (CkMethodWriteOutcome.IsUnknown(e))
        {
            logger.LogError(start.Redact(e),
                "CK method operation {OperationId} outcome unknown: a write was not acknowledged by the database (correlation {CorrelationId})",
                record.OperationId, correlationId);
            completion = Failed(Error(CkMethodErrorCodes.MethodOutcomeUnknown, record.MethodId,
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
        }
        catch (Exception e)
        {
            logger.LogError(start.Redact(e), "CK method operation {OperationId} failed (correlation {CorrelationId})",
                record.OperationId, correlationId);
            completion = Failed(Error(CkMethodErrorCodes.MethodHandlerFailed, record.MethodId,
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
        }

        await CompleteAsync(record, completion).ConfigureAwait(false);
    }

    private async Task CompleteAsync(CkMethodOperationRecord record, CkMethodOperationCompletion completion)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CompletionTimeout);
            var written = await store.CompleteAsync(record.TenantId, record.OperationId, completion, timeout.Token)
                .ConfigureAwait(false);
            if (written)
            {
                CkMethodMetrics.RecordOperation(record.MethodId, completion.State);
                logger.LogInformation("CK method operation {OperationId} finished: {State} {Code}",
                    record.OperationId, completion.State, completion.Error?.Code ?? "-");
            }
            else
            {
                logger.LogWarning(
                    "CK method operation {OperationId} was already final (ended as lost); its late outcome {State} is dropped",
                    record.OperationId, completion.State);
            }
        }
        catch (Exception e)
        {
            // the record stays open; without heartbeats the sweep ends it as lost
            logger.LogError("CK method operation {OperationId}: the final state could not be written ({ExceptionType})",
                record.OperationId, e.GetType().Name);
        }
    }

    /// <summary>Refreshes the heartbeat of every operation this host works on. Never throws.</summary>
    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (tenantId, operationId) in _running.Keys)
        {
            try
            {
                await store.HeartbeatAsync(tenantId, operationId, now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning("CK method operation {OperationId}: heartbeat failed ({ExceptionType})",
                    operationId, e.GetType().Name);
            }
        }
    }

    /// <summary>Ends lost operations of any host (no heartbeat for <c>StaleAfter</c>). Never throws.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var ended = await store.FailLostAsync(now - Options.StaleAfter, now, Options.Retention, cancellationToken)
                .ConfigureAwait(false);
            if (ended > 0)
            {
                CkMethodMetrics.RecordOperation(null, CkMethodOperationState.Failed, ended);
                logger.LogWarning(
                    "{Count} CK method operation(s) ended as {Code} ({Reason}): no heartbeat for {StaleAfter}",
                    ended, CkMethodErrorCodes.MethodOutcomeUnknown, CkMethodOperationReasons.HandlerLost,
                    Options.StaleAfter);
            }

            return ended;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception e)
        {
            logger.LogWarning("CK method operation sweep failed ({ExceptionType})", e.GetType().Name);
            return 0;
        }
    }

    /// <summary>
    ///     Graceful stop: cancels the work of every running operation (it ends as lost, <c>METHOD_OUTCOME_UNKNOWN</c>) and
    ///     waits for it until <paramref name="cancellationToken" /> fires. Work that ignores its token is left to the sweep.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        var running = _running.Values.ToArray();
        if (running.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("{Count} CK method operation(s) did not stop in time; the sweep ends them",
                _running.Count);
        }
    }

    /// <summary>Waits until the operation's work and its final write are done (tests).</summary>
    internal Task WhenFinishedAsync(string tenantId, string operationId) =>
        _running.TryGetValue((CkMethodInMemoryOperationStore.Normalize(tenantId), operationId), out var task)
            ? task
            : Task.CompletedTask;

    private CkMethodOperationCompletion Succeeded(string? resultJson)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded,
            ResultJson = resultJson,
            CompletedAtUtc = now,
            ExpiresAtUtc = now + Options.Retention
        };
    }

    private CkMethodOperationCompletion Failed(CkMethodError error)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Failed,
            Error = error,
            CompletedAtUtc = now,
            ExpiresAtUtc = now + Options.Retention
        };
    }

    private CkMethodOperationCompletion Lost(string methodId) =>
        Failed(Error(CkMethodErrorCodes.MethodOutcomeUnknown, methodId,
            (CkMethodErrorParameters.Reason, CkMethodOperationReasons.HandlerLost)));

    private static CkMethodError Error(string code, string methodId, params (string Name, string Value)[] parameters) =>
        CkMethodError.Create(code, [(CkMethodErrorParameters.MethodId, methodId), .. parameters]);

    private sealed class Reporter(
        ICkMethodOperationStore store,
        TimeProvider timeProvider,
        ILogger logger,
        CkMethodOperationRecord record)
        : ICkMethodOperationProgress
    {
        public string OperationId => record.OperationId;

        public async Task ReportAsync(CkMethodOperationProgress progress, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(progress);
            progress.Validate();
            try
            {
                await store.ReportProgressAsync(record.TenantId, record.OperationId, progress,
                    timeProvider.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // reporting never fails the work
                logger.LogWarning("CK method operation {OperationId}: progress not stored ({ExceptionType})",
                    record.OperationId, e.GetType().Name);
            }
        }
    }
}
