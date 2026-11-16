namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     AB#8881 (Feature AB#5694 F3.6): an <see cref="ICkMethodOperationStore" /> that never shows a tenant the records of an
///     earlier tenant with the same id. Every read compares the record's <see cref="CkMethodOperationRecord.AcceptedAtUtc" />
///     with the creation time of the current tenant (<see cref="ICkMethodOperationTenantGeneration" />): a record accepted
///     before it is invisible to <see cref="GetAsync" /> and <see cref="ListAsync" />. This closes the two gaps the purge on
///     delete (<see cref="ICkMethodOperationTenantPurge" />) leaves open:
///     <list type="bullet">
///         <item><description>the purge failed, so the old records stay until they expire;</description></item>
///         <item><description>a call admitted just before the delete wrote its record after the purge (no block-and-drain across hosts).</description></item>
///     </list>
///     The store registrations (<c>AddCkMethodMongoOperationStore</c>, <c>AddCkMethodInMemoryOperationStore</c>) wrap the
///     store with it; a host that registers its own <see cref="ICkMethodOperationStore" /> wraps it itself.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item><description><b>Writes</b> (create, progress, heartbeat, completion, sweep) go to the inner store unchanged: the handler that accepted an operation keeps updating its own record.</description></item>
///         <item><description><b>Unknown generation</b> (<c>null</c>: no lifecycle record) filters nothing, as before AB#8881.</description></item>
///         <item><description><b>Fails closed</b>: a generation that cannot be read is a <see cref="CkMethodOperationStoreException" />, never "no filter".</description></item>
///         <item><description><b>Clocks</b>: the accept time comes from the handler host, the creation time from the asset repository. A tenant id is free again only after the delete has settled (about 90 s after the drop), so clock skew below that does not let an old record through.</description></item>
///     </list>
/// </remarks>
public sealed class CkMethodTenantGenerationOperationStore(
    ICkMethodOperationStore inner,
    ICkMethodOperationTenantGeneration generation)
    : ICkMethodOperationStore, ICkMethodOperationTenantPurge
{
    /// <summary>The wrapped store.</summary>
    public ICkMethodOperationStore Inner { get; } = inner;

    /// <inheritdoc />
    public Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default) =>
        Inner.CreateAsync(record, cancellationToken);

    /// <inheritdoc />
    public async Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
        CancellationToken cancellationToken = default)
    {
        var record = await Inner.GetAsync(tenantId, operationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var start = await generation.GetStartUtcAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return start is { } s && record.AcceptedAtUtc < s ? null : record;
    }

    /// <inheritdoc />
    public async Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var start = await generation.GetStartUtcAsync(query.TenantId, cancellationToken).ConfigureAwait(false);
        if (start is { } s && (query.SinceUtc is null || query.SinceUtc < s))
        {
            // the store filters, so paging and the total count stay right
            query = query with { SinceUtc = s };
        }

        return await Inner.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Inner.MarkRunningAsync(tenantId, operationId, nowUtc, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Inner.ReportProgressAsync(tenantId, operationId, progress, nowUtc, cancellationToken);

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Inner.HeartbeatAsync(tenantId, operationId, nowUtc, cancellationToken);

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
        CancellationToken cancellationToken = default) =>
        Inner.CompleteAsync(tenantId, operationId, completion, cancellationToken);

    /// <inheritdoc />
    public Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
        CancellationToken cancellationToken = default) =>
        Inner.FailLostAsync(staleBeforeUtc, nowUtc, retention, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Removes nothing when the inner store has no purge.</remarks>
    public Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Inner is ICkMethodOperationTenantPurge purge
            ? purge.DeleteTenantAsync(tenantId, cancellationToken)
            : NothingRemoved(tenantId);

    /// <inheritdoc />
    /// <remarks>Removes nothing when the inner store has no purge.</remarks>
    public Task<long> DeleteTenantAcceptedBeforeAsync(string tenantId, DateTime acceptedBeforeUtc,
        CancellationToken cancellationToken = default) =>
        Inner is ICkMethodOperationTenantPurge purge
            ? purge.DeleteTenantAcceptedBeforeAsync(tenantId, acceptedBeforeUtc, cancellationToken)
            : NothingRemoved(tenantId);

    private static Task<long> NothingRemoved(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Task.FromResult(0L);
    }
}
