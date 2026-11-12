namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     Removes every operation record of a tenant (AB#8447, Feature AB#5694 F3.6): when a tenant is deleted, its records in
///     the store must go with it, so that a new tenant with the same id never reads an operation of the old one. Open
///     records are removed too; their work, if still running, then completes into nothing (the first final state wins, a
///     missing record is not written again).
/// </summary>
/// <remarks>
///     A separate interface (not a member of <see cref="ICkMethodOperationStore" />), so the store contract and every
///     implementation outside this repository stay unchanged. <see cref="CkMethodInMemoryOperationStore" /> and the MongoDB
///     store implement it; the store registrations (<c>AddCkMethodInMemoryOperationStore</c>,
///     <c>AddCkMethodMongoOperationStore</c>) register it, resolving to the registered store when it implements this
///     interface and to a no-op otherwise.
///     <para>
///         A <b>restore</b> replaces the tenant database, not the tenant: it must not purge. The records live in the system
///         database, so the restore's own operation stays readable while and after the tenant database is replaced.
///     </para>
/// </remarks>
public interface ICkMethodOperationTenantPurge
{
    /// <summary>Removes all records of <paramref name="tenantId" /> (any state, expired or not).</summary>
    /// <returns>The number of records removed.</returns>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    /// <exception cref="CkMethodOperationStoreException">The store could not be written.</exception>
    Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The purge of a store that keeps no records of its own (a custom <see cref="ICkMethodOperationStore" /> that does not
///     implement <see cref="ICkMethodOperationTenantPurge" />): removes nothing.
/// </summary>
public sealed class CkMethodNoOperationTenantPurge : ICkMethodOperationTenantPurge
{
    /// <inheritdoc />
    public Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Task.FromResult(0L);
    }
}
