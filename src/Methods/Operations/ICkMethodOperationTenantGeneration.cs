using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     The generation of a tenant id (AB#8881, Feature AB#5694 F3.6): the time (UTC) the current tenant with this id was
///     created. A tenant id can be deleted and created again; operation records accepted before the current tenant was
///     created belong to an earlier tenant with the same id and must never be read by the current one.
///     <see cref="CkMethodTenantGenerationOperationStore" /> compares every read against it, so a record that a failed
///     purge left behind, or that a call admitted just before the delete wrote after the purge, stays invisible.
/// </summary>
public interface ICkMethodOperationTenantGeneration
{
    /// <summary>
    ///     The creation time (UTC) of the current tenant with <paramref name="tenantId" />, or <c>null</c> when it is
    ///     unknown (no lifecycle record: a legacy tenant, or a host without the tenant lifecycle store). <c>null</c> means
    ///     "no generation filter".
    /// </summary>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    /// <exception cref="CkMethodOperationStoreException">The generation could not be read.</exception>
    Task<DateTime?> GetStartUtcAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>No generation source: every tenant id has an unknown generation, reads are not filtered (tests, hosts without the engine).</summary>
public sealed class CkMethodNoOperationTenantGeneration : ICkMethodOperationTenantGeneration
{
    /// <inheritdoc />
    public Task<DateTime?> GetStartUtcAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Task.FromResult<DateTime?>(null);
    }
}

/// <summary>
///     The generation from the tenant lifecycle record (<see cref="ITenantLifecycleStore" />, system database): its
///     <see cref="TenantLifecycleRecord.CreatedUtc" />. The asset repository writes the record when the setup of a new
///     tenant starts (a new creation cycle sets <c>CreatedUtc</c>); a restore keeps it, a delete keeps it as the tombstone
///     until the delete has settled and then removes it, so a tenant created again with the same id gets a new one. The
///     create endpoint refuses the id while the tombstone lives (about 90 s after the drop).
/// </summary>
public sealed class CkMethodTenantLifecycleGeneration(ITenantLifecycleStore lifecycleStore)
    : ICkMethodOperationTenantGeneration
{
    /// <inheritdoc />
    public async Task<DateTime?> GetStartUtcAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var normalized = CkMethodOperationTenant.Normalize(tenantId);
        TenantLifecycleRecord? record;
        try
        {
            record = await lifecycleStore.GetAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // fail closed: a read must not fall back to "no filter" because the generation is unreadable
            throw new CkMethodOperationStoreException(
                "The tenant generation could not be read: " + ex.GetType().Name, ex);
        }

        if (record is null || record.CreatedUtc == default)
        {
            return null;
        }

        return record.CreatedUtc.Kind switch
        {
            DateTimeKind.Utc => record.CreatedUtc,
            DateTimeKind.Local => record.CreatedUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(record.CreatedUtc, DateTimeKind.Utc)
        };
    }
}
