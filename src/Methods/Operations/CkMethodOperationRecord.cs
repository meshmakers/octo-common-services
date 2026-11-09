using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     State of an operation of an asynchronous CK method (F3.6, OD-5). <see cref="Accepted" /> and
///     <see cref="Running" /> are open, <see cref="Succeeded" /> and <see cref="Failed" /> are final: a final record never
///     changes again.
/// </summary>
public enum CkMethodOperationState
{
    /// <summary>The record is written and the handle was (or is about to be) returned; the work has not started yet.</summary>
    Accepted = 0,

    /// <summary>The handler works on the operation and sends heartbeats.</summary>
    Running = 1,

    /// <summary>The operation finished successfully; the record holds the result (unless the result is sensitive).</summary>
    Succeeded = 2,

    /// <summary>
    ///     The operation failed; the record holds the error. A lost handler ends as <c>METHOD_OUTCOME_UNKNOWN</c> with the
    ///     reason <see cref="CkMethodOperationReasons.HandlerLost" />.
    /// </summary>
    Failed = 3
}

/// <summary>Reasons (error parameter <c>reason</c>) the operation runtime adds to platform codes.</summary>
public static class CkMethodOperationReasons
{
    /// <summary>
    ///     The handler instance stopped (killed, crashed, shut down) before it finished the operation; whether the work
    ///     took effect is unknown (<c>METHOD_OUTCOME_UNKNOWN</c>). The work is never started again (at most once).
    /// </summary>
    public const string HandlerLost = "handler-lost";

    /// <summary>
    ///     The operation record could not be written, so the call was not accepted and the work was not started
    ///     (<c>METHOD_HANDLER_NOT_AVAILABLE</c>).
    /// </summary>
    public const string StoreUnavailable = "operation-store-unavailable";
}

/// <summary>
///     Progress of a running operation, language-neutral: an optional percentage, an optional stage code and parameters
///     the client renders in its own language (like <see cref="CkMethodError" />, there is no message text).
/// </summary>
public sealed record CkMethodOperationProgress
{
    /// <summary>Longest stage code.</summary>
    public const int MaxStageCodeLength = 64;

    /// <summary>Most parameters per progress report.</summary>
    public const int MaxParameters = 16;

    /// <summary>Done in percent, 0 to 100; <c>null</c> when the handler cannot tell.</summary>
    public int? Percent { get; init; }

    /// <summary>
    ///     Stable, language-neutral code of the current stage (UPPER_SNAKE_CASE, e.g. <c>COPYING_COLLECTIONS</c>);
    ///     <c>null</c> when the handler reports only a percentage.
    /// </summary>
    public string? StageCode { get; init; }

    /// <summary>
    ///     Parameters of the stage (e.g. <c>collection</c>, <c>done</c>, <c>total</c>). Values are plain strings and never
    ///     contain method parameter values.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates a validated progress report.</summary>
    /// <param name="percent">Done in percent (0 to 100) or <c>null</c>.</param>
    /// <param name="stageCode">Stage code (UPPER_SNAKE_CASE, at most 64 characters) or <c>null</c>.</param>
    /// <param name="parameters">Name/value pairs of the stage (at most 16).</param>
    /// <exception cref="ArgumentException">A value is out of range or the code has the wrong shape.</exception>
    public static CkMethodOperationProgress Create(int? percent, string? stageCode = null,
        params (string Name, string Value)[] parameters)
    {
        var progress = new CkMethodOperationProgress
        {
            Percent = percent,
            StageCode = stageCode,
            Parameters = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
        };
        progress.Validate();
        return progress;
    }

    /// <summary>Checks the ranges; the store and the reporter call it before a report is written.</summary>
    /// <exception cref="ArgumentException">A value is out of range or the code has the wrong shape.</exception>
    public void Validate()
    {
        if (Percent is < 0 or > 100)
        {
            throw new ArgumentException("The progress percentage must be between 0 and 100.", nameof(Percent));
        }

        if (StageCode is not null && !IsValidStageCode(StageCode))
        {
            throw new ArgumentException(
                "The stage code must be UPPER_SNAKE_CASE with 3 to 64 characters.", nameof(StageCode));
        }

        if (Parameters.Count > MaxParameters)
        {
            throw new ArgumentException($"A progress report carries at most {MaxParameters} parameters.",
                nameof(Parameters));
        }
    }

    private static bool IsValidStageCode(string code) =>
        code.Length is >= 3 and <= MaxStageCodeLength &&
        code[0] is >= 'A' and <= 'Z' &&
        code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

/// <summary>
///     Who started an operation: the <c>sub</c> of a user, or the <c>client_id</c> of a client-credentials caller (no
///     <c>sub</c>), taken from the trusted principal of the call (never from <c>CkMethodRequest.Caller</c>). Both empty
///     when the handler host established no principal.
/// </summary>
/// <param name="SubjectId">The <c>sub</c> of a user caller.</param>
/// <param name="ClientId">The <c>client_id</c> of a service caller without <c>sub</c>.</param>
public sealed record CkMethodOperationOwner(string? SubjectId, string? ClientId)
{
    /// <summary>No principal.</summary>
    public static CkMethodOperationOwner None { get; } = new(null, null);

    /// <summary>The owner of a call made by <paramref name="principal" />: the subject, else the client id.</summary>
    public static CkMethodOperationOwner Of(CkMethodPrincipal? principal) =>
        principal is null
            ? None
            : !string.IsNullOrWhiteSpace(principal.SubjectId)
                ? new CkMethodOperationOwner(principal.SubjectId, null)
                : new CkMethodOperationOwner(null, string.IsNullOrWhiteSpace(principal.ClientId) ? null : principal.ClientId);

    /// <summary><c>true</c> when neither a subject nor a client id is known.</summary>
    public bool IsNone => SubjectId is null && ClientId is null;
}

/// <summary>
///     The durable record of one operation of an asynchronous CK method (AB#8443, OD-5): written before the accepted
///     envelope leaves the handler host, updated with progress and heartbeats while the handler works, and final once it
///     succeeded or failed. It is kept until <see cref="ExpiresAtUtc" /> (completion plus <c>Methods:Operations:Retention</c>).
///     <para>
///         <b>Never stored:</b> the method parameters, the caller's access token (OD-10: the principal stays in the process)
///         and a result the CK model marks <c>sensitive</c>. <see cref="ToString" /> prints ids and the state only.
///     </para>
/// </summary>
public sealed record CkMethodOperationRecord
{
    /// <summary>Tenant of the operation (stored lower-case); every read names it.</summary>
    public required string TenantId { get; init; }

    /// <summary>Opaque id of the operation, unique in the tenant (<see cref="CkMethodOperationHandle.OperationId" />).</summary>
    public required string OperationId { get; init; }

    /// <summary>Qualified, model-version-less method id.</summary>
    public required string MethodId { get; init; }

    /// <summary>Id of the invocation that started the operation.</summary>
    public required Guid InvocationId { get; init; }

    /// <summary>Id of the request that caused the call, if any.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The caller that started the operation.</summary>
    public CkMethodOperationOwner Owner { get; init; } = CkMethodOperationOwner.None;

    /// <summary>Instance id of the handler that works on the operation.</summary>
    public required string HandlerInstanceId { get; init; }

    /// <summary>Current state.</summary>
    public CkMethodOperationState State { get; init; }

    /// <summary>Last progress report, if any.</summary>
    public CkMethodOperationProgress? Progress { get; init; }

    /// <summary>
    ///     Result as camelCase JSON of a <see cref="CkMethodOperationState.Succeeded" /> operation of a method with a result;
    ///     always <c>null</c> for a result the CK model marks sensitive.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>The error of a <see cref="CkMethodOperationState.Failed" /> operation (code and parameters).</summary>
    public CkMethodError? Error { get; init; }

    /// <summary>Time (UTC) the operation was accepted.</summary>
    public required DateTime AcceptedAtUtc { get; init; }

    /// <summary>Time (UTC) the handler started the work.</summary>
    public DateTime? StartedAtUtc { get; init; }

    /// <summary>Time (UTC) of the last sign of life of the handler (accept, start, progress or heartbeat).</summary>
    public required DateTime HeartbeatAtUtc { get; init; }

    /// <summary>Time (UTC) the operation became final.</summary>
    public DateTime? CompletedAtUtc { get; init; }

    /// <summary>
    ///     Time (UTC) after which the record is forgotten: completion plus the retention. <c>null</c> while the operation is
    ///     open (a lost handler is ended by the sweep, which sets it).
    /// </summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary><c>true</c> for <see cref="CkMethodOperationState.Succeeded" /> and <see cref="CkMethodOperationState.Failed" />.</summary>
    public bool IsFinal => State is CkMethodOperationState.Succeeded or CkMethodOperationState.Failed;

    /// <summary><c>true</c> when the record is past <see cref="ExpiresAtUtc" /> at <paramref name="nowUtc" />.</summary>
    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc is { } expires && expires <= nowUtc;

    /// <summary>Redacting representation: ids and the state, never the result.</summary>
    public override string ToString() =>
        $"CkMethodOperationRecord {{ {TenantId}/{OperationId} {MethodId} state={State} error={Error?.Code ?? "-"} " +
        $"hasResult={ResultJson is not null} }}";
}

/// <summary>The final outcome written by <see cref="ICkMethodOperationStore.CompleteAsync" />.</summary>
public sealed record CkMethodOperationCompletion
{
    /// <summary><see cref="CkMethodOperationState.Succeeded" /> or <see cref="CkMethodOperationState.Failed" />.</summary>
    public required CkMethodOperationState State { get; init; }

    /// <summary>Result JSON of a success (never a sensitive result).</summary>
    public string? ResultJson { get; init; }

    /// <summary>The error of a failure.</summary>
    public CkMethodError? Error { get; init; }

    /// <summary>Time (UTC) of the completion.</summary>
    public required DateTime CompletedAtUtc { get; init; }

    /// <summary>Time (UTC) the record expires: <see cref="CompletedAtUtc" /> plus the retention.</summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>Checks the shape.</summary>
    /// <exception cref="ArgumentException">Not a final state, a failure without error or a success with error.</exception>
    public void Validate()
    {
        switch (State)
        {
            case CkMethodOperationState.Succeeded when Error is not null:
                throw new ArgumentException("A succeeded operation carries no error.", nameof(Error));
            case CkMethodOperationState.Failed when Error is null:
                throw new ArgumentException("A failed operation needs the error.", nameof(Error));
            case CkMethodOperationState.Failed when ResultJson is not null:
                throw new ArgumentException("A failed operation carries no result.", nameof(ResultJson));
            case CkMethodOperationState.Accepted or CkMethodOperationState.Running:
                throw new ArgumentException("An operation is completed as Succeeded or Failed.", nameof(State));
        }
    }

    /// <summary>Redacting representation: never the result.</summary>
    public override string ToString() =>
        $"CkMethodOperationCompletion {{ state={State} error={Error?.Code ?? "-"} hasResult={ResultJson is not null} }}";
}

/// <summary>
///     Filter of <see cref="ICkMethodOperationStore.ListAsync" />: always one tenant, optionally the owner, the method, the
///     state and a lower bound of the accept time; paged by <see cref="Skip" /> and <see cref="Take" />, newest first.
/// </summary>
public sealed record CkMethodOperationQuery
{
    /// <summary>Largest page.</summary>
    public const int MaxTake = 200;

    /// <summary>Default page size.</summary>
    public const int DefaultTake = 50;

    /// <summary>The tenant; required.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    ///     Only operations of this owner: a subject matches <see cref="CkMethodOperationOwner.SubjectId" />, a client id
    ///     (without subject) matches <see cref="CkMethodOperationOwner.ClientId" /> of an operation without subject.
    ///     <c>null</c> = every owner (tenant administrators).
    /// </summary>
    public CkMethodOperationOwner? Owner { get; init; }

    /// <summary>Only operations of this method.</summary>
    public string? MethodId { get; init; }

    /// <summary>Only operations in this state.</summary>
    public CkMethodOperationState? State { get; init; }

    /// <summary>Only operations accepted at or after this time (UTC).</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Records to skip (0 or more).</summary>
    public int Skip { get; init; }

    /// <summary>Page size, 1 to <see cref="MaxTake" />.</summary>
    public int Take { get; init; } = DefaultTake;

    /// <summary>Checks the paging values and the tenant.</summary>
    /// <exception cref="ArgumentException">The tenant is empty or a paging value is out of range.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantId);
        if (Skip < 0)
        {
            throw new ArgumentException("Skip must not be negative.", nameof(Skip));
        }

        if (Take is < 1 or > MaxTake)
        {
            throw new ArgumentException($"Take must be between 1 and {MaxTake}.", nameof(Take));
        }

        if (Owner is { IsNone: true })
        {
            throw new ArgumentException("An owner filter needs a subject or a client id.", nameof(Owner));
        }
    }
}

/// <summary>One page of <see cref="ICkMethodOperationStore.ListAsync" />.</summary>
/// <param name="Items">The records of the page, newest accepted first.</param>
/// <param name="TotalCount">Number of records that match the filter (all pages).</param>
public sealed record CkMethodOperationPage(IReadOnlyList<CkMethodOperationRecord> Items, long TotalCount);

/// <summary>How every operation store keys a tenant: trimmed and lower-case, so route and request spellings meet.</summary>
public static class CkMethodOperationTenant
{
    /// <summary>The stored form of <paramref name="tenantId" />.</summary>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    public static string Normalize(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return tenantId.Trim().ToLowerInvariant();
    }
}
