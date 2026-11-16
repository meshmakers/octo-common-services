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
///     <para>
///         A purge can fail, and a call admitted just before the delete can write its record after the purge. Neither may
///         show an old record to a new tenant with the same id: reads compare every record against the tenant's generation
///         (AB#8881, <see cref="CkMethodTenantGenerationOperationStore" />), so the purge is cleanup, not the guarantee.
///     </para>
/// </remarks>
public interface ICkMethodOperationTenantPurge
{
    /// <summary>Removes all records of <paramref name="tenantId" /> (any state, expired or not).</summary>
    /// <returns>The number of records removed.</returns>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    /// <exception cref="CkMethodOperationStoreException">The store could not be written.</exception>
    Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     AB#8881: removes the records of <paramref name="tenantId" /> accepted before <paramref name="acceptedBeforeUtc" />
    ///     (any state, expired or not): the records of an earlier tenant with the same id, when the setup of the current
    ///     tenant knows its creation time (<see cref="ICkMethodOperationTenantGeneration" />). Cleanup only: reads already
    ///     ignore those records (<see cref="CkMethodTenantGenerationOperationStore" />).
    /// </summary>
    /// <remarks>
    ///     The default implementation removes nothing (a store without records of its own, or a custom store written before
    ///     AB#8881); its records then expire with their retention and stay invisible to the current tenant.
    /// </remarks>
    /// <returns>The number of records removed.</returns>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    /// <exception cref="CkMethodOperationStoreException">The store could not be written.</exception>
    Task<long> DeleteTenantAcceptedBeforeAsync(string tenantId, DateTime acceptedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Task.FromResult(0L);
    }
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
