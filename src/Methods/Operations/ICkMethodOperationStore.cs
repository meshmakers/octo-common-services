namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     Durable store of the operation records of asynchronous CK methods (AB#8443, OD-5). One implementation serves the
///     writing side (the handler host, bot-services AB#8445) and the reading side (the gateway's <c>runtime.operations</c>,
///     AB#8444): <see cref="CkMethodInMemoryOperationStore" /> for tests and single-replica hosts, the MongoDB store of the
///     package <c>Meshmakers.Octo.Services.Methods.MongoDb</c> (system database) everywhere else.
///     <para>
///         <b>Rules every implementation keeps</b> (pinned by the contract tests):
///     </para>
///     <list type="bullet">
///         <item><description>Every read names the tenant; a read for tenant B never returns a record of tenant A. Tenant ids compare case-insensitively (stored lower-case).</description></item>
///         <item><description>A record past its <see cref="CkMethodOperationRecord.ExpiresAtUtc" /> is invisible to <see cref="GetAsync" /> and <see cref="ListAsync" />, also before housekeeping removes it.</description></item>
///         <item><description>A final record (<see cref="CkMethodOperationState.Succeeded" />, <see cref="CkMethodOperationState.Failed" />) never changes again: progress, heartbeat, start and completion are conditional on an open record.</description></item>
///         <item><description>Writes fail closed: a database problem is thrown as <see cref="CkMethodOperationStoreException" />, never answered on a guess.</description></item>
///     </list>
/// </summary>
public interface ICkMethodOperationStore
{
    /// <summary>
    ///     Writes a new record (state <see cref="CkMethodOperationState.Accepted" />). The handler host calls it before the
    ///     accepted envelope is sent; when it throws, the call is not accepted and the work is not started.
    /// </summary>
    /// <exception cref="CkMethodOperationStoreException">
    ///     The record could not be written, or an operation with the same id exists in the tenant.
    /// </exception>
    Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default);

    /// <summary>The record of an operation, or <c>null</c> when it is unknown, expired or of another tenant.</summary>
    Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>A page of the tenant's records that match <paramref name="query" />, newest accepted first.</summary>
    /// <exception cref="ArgumentException">The query is invalid (<see cref="CkMethodOperationQuery.Validate" />).</exception>
    Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves an <see cref="CkMethodOperationState.Accepted" /> record to <see cref="CkMethodOperationState.Running" />
    ///     and refreshes the heartbeat. <c>false</c> when the record is not accepted any more (e.g. the sweep ended it).
    /// </summary>
    Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a progress report and refreshes the heartbeat of an open record. <c>false</c> when it is final.</summary>
    Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
        DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Refreshes the heartbeat of an open record. <c>false</c> when it is final or unknown.</summary>
    Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Makes an open record final. <c>false</c> when it is final already (the sweep ended it as lost): the first final
    ///     state wins.
    /// </summary>
    /// <exception cref="ArgumentException">The completion is not a final shape (<see cref="CkMethodOperationCompletion.Validate" />).</exception>
    Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The sweep: ends every open record (all tenants) whose heartbeat is older than <paramref name="staleBeforeUtc" />
    ///     as <see cref="CkMethodOperationState.Failed" /> with <c>METHOD_OUTCOME_UNKNOWN</c> and reason
    ///     <see cref="CkMethodOperationReasons.HandlerLost" />, completed at <paramref name="nowUtc" /> and kept for
    ///     <paramref name="retention" />. The work is never started again.
    /// </summary>
    /// <returns>Number of records ended.</returns>
    Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
        CancellationToken cancellationToken = default);
}

/// <summary>The operation store could not do what was asked (database down, duplicate id); the caller must fail closed.</summary>
public sealed class CkMethodOperationStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CkMethodOperationStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Progress reporter handed to the work of an accepted operation (<see cref="CkMethodOutcome.Accepted" />). A report
///     is written to the operation record and counts as a heartbeat. Reporting never fails the work: a store problem is
///     logged and the next report or heartbeat tries again.
/// </summary>
public interface ICkMethodOperationProgress
{
    /// <summary>Id of the operation (the handle the caller got).</summary>
    string OperationId { get; }

    /// <summary>Stores a progress report.</summary>
    /// <exception cref="ArgumentException">The report is invalid (<see cref="CkMethodOperationProgress.Validate" />).</exception>
    Task ReportAsync(CkMethodOperationProgress progress, CancellationToken cancellationToken = default);
}
