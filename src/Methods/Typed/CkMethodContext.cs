using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     What a handler knows about one invocation: who called which method on which target, in which tenant, until when.
///     It deliberately offers no database session and no transaction: a method runs outside the request transaction of the
///     caller (see the consistency guide), a handler opens its own units of work.
///     <see cref="ToString" /> never prints parameters or the access token.
/// </summary>
public class CkMethodContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a context for an invocation. Handler authors use this to unit-test their handler.</summary>
    /// <param name="request">The wire request.</param>
    /// <param name="timeProvider">Clock used for <see cref="RemainingTime" />; defaults to the system clock.</param>
    public CkMethodContext(CkMethodRequest request, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The wire request. Do not log it beyond its (redacting) <c>ToString()</c>.</summary>
    public CkMethodRequest Request { get; }

    /// <summary>Unique id of this invocation; correlates caller, handler, logs and audit.</summary>
    public Guid InvocationId => Request.InvocationId;

    /// <summary>Id of the request that caused the call (GraphQL request, pipeline execution), if any.</summary>
    public Guid? CorrelationId => Request.CorrelationId;

    /// <summary>Tenant the method runs in.</summary>
    public string TenantId => Request.TenantId;

    /// <summary>Qualified, model-version-less method id, e.g. <c>System.Identity/User.ChangePassword-1</c>.</summary>
    public string MethodId => Request.MethodId;

    /// <summary>Concrete CK type id of the target entity.</summary>
    public string CkTypeId => Request.CkTypeId;

    /// <summary>Runtime id of the target entity; null for static methods.</summary>
    public string? TargetRtId => Request.TargetRtId;

    /// <summary>
    ///     Informational caller identity. Never authorize on it: it is not authenticated on the bus.
    ///     Re-validate <see cref="CallerAccessToken" /> instead.
    /// </summary>
    public CkMethodCaller? Caller => Request.Caller;

    /// <summary>Raw JWT of the caller for the handler to re-validate. Sensitive: never log it.</summary>
    public string? CallerAccessToken => Request.CallerAccessToken;

    /// <summary>Client key for safe retries of non-idempotent methods.</summary>
    public string? IdempotencyKey => Request.IdempotencyKey;

    /// <summary>Absolute deadline (UTC). The cancellation token of the handler is cancelled when it passes.</summary>
    public DateTime DeadlineUtc => Request.DeadlineUtc;

    /// <summary>Time left until <see cref="DeadlineUtc" />; <see cref="TimeSpan.Zero" /> when it has passed.</summary>
    public TimeSpan RemainingTime
    {
        get
        {
            var remaining = Request.DeadlineUtc - _timeProvider.GetUtcNow().UtcDateTime;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"CkMethodContext {{ {MethodId} {TenantId}/{TargetRtId} {InvocationId} }}";
}

/// <summary>A <see cref="CkMethodContext" /> with the deserialized, typed parameters.</summary>
/// <typeparam name="TParameters">The generated parameter record (<c>{Type}{Method}Parameters</c>).</typeparam>
public sealed class CkMethodContext<TParameters> : CkMethodContext
    where TParameters : class
{
    /// <summary>Creates a context with parameters. Handler authors use this to unit-test their handler.</summary>
    public CkMethodContext(CkMethodRequest request, TParameters parameters, TimeProvider? timeProvider = null)
        : base(request, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = parameters;
    }

    /// <summary>The method parameters. Sensitive members must not be logged by the handler either.</summary>
    public TParameters Parameters { get; }
}
