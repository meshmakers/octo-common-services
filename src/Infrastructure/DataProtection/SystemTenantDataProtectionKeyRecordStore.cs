using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>
///     Stores Data Protection key ring elements as <c>System.Identity/DataProtectionKey</c> entities in the system tenant
///     (the collection the identity service keeps its own key ring in), through the admin repository of
///     <see cref="ISystemContext" />. Every replica of every service of the installation sees the same store.
/// </summary>
/// <remarks>
///     Data Protection runs outside any HTTP request, so the per-request tenant resolver cannot be used. Nothing is
///     written before the system tenant exists (a write into a system database the bootstrap has not created yet would
///     materialize a collection the bootstrap does not expect), and nothing is read or written while the system tenant
///     has no <c>System.Identity</c> construction kit yet.
/// </remarks>
internal sealed class SystemTenantDataProtectionKeyRecordStore(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<SystemTenantDataProtectionKeyRecordStore> logger) : IDataProtectionKeyRecordStore
{
    private static readonly RtCkId<CkTypeId> KeyCkTypeId = new(DataProtectionKeyXml.KeyCkTypeId);

    /// <inheritdoc />
    public async Task<bool> CanPersistAsync()
    {
        using var scope = serviceScopeFactory.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        if (!await systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return false;
        }

        return await IsKeyTypeAvailableAsync(systemContext.GetSystemTenantRepositoryAsAdmin()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataProtectionKeyRecord>> LoadAllAsync()
    {
        using var scope = serviceScopeFactory.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        if (!await systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return [];
        }

        var repository = systemContext.GetSystemTenantRepositoryAsAdmin();
        if (!await IsKeyTypeAvailableAsync(repository).ConfigureAwait(false))
        {
            return [];
        }

        using var session = await repository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByTypeAsync(session, KeyCkTypeId, RtEntityQueryOptions.Create())
            .ConfigureAwait(false);
        await session.CommitTransactionAsync().ConfigureAwait(false);

        return result.Items
            .Select(entity => new DataProtectionKeyRecord(
                entity.GetAttributeStringValueOrDefault(DataProtectionKeyXml.FriendlyNameAttributeName) ?? string.Empty,
                entity.GetAttributeStringValueOrDefault(DataProtectionKeyXml.XmlDataAttributeName) ?? string.Empty))
            .Where(record => record.StoredName.Length > 0 && record.XmlData.Length > 0)
            .ToList();
    }

    /// <inheritdoc />
    public async Task InsertAsync(DataProtectionKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var scope = serviceScopeFactory.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        if (!await systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Data Protection key '{record.StoredName}' cannot be persisted before the system tenant exists.");
        }

        var repository = systemContext.GetSystemTenantRepositoryAsAdmin();
        try
        {
            using var session = await repository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            if (await ExistsAsync(repository, session, record.StoredName).ConfigureAwait(false))
            {
                await session.CommitTransactionAsync().ConfigureAwait(false);
                return;
            }

            var entity = new RtEntity(KeyCkTypeId, OctoObjectId.GenerateNewId());
            entity.SetAttributeValue(DataProtectionKeyXml.FriendlyNameAttributeName, AttributeValueTypesDto.String,
                record.StoredName);
            entity.SetAttributeValue(DataProtectionKeyXml.XmlDataAttributeName, AttributeValueTypesDto.String,
                record.XmlData);
            entity.SetAttributeValue(DataProtectionKeyXml.CreationDateTimeAttributeName,
                AttributeValueTypesDto.DateTime, DateTime.UtcNow);
            await repository.InsertOneRtEntityAsync(session, KeyCkTypeId, entity).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unique index on the name: a concurrent replica won the race and the element IS stored, which is all
            // Data Protection needs. Anything else is a real error and is rethrown unchanged.
            logger.LogWarning("Storing Data Protection key '{StoredName}' failed ({Message}); verifying",
                record.StoredName, ex.Message);
            if (!await IsStoredAsync(repository, record.StoredName).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task<bool> IsStoredAsync(ITenantRepository repository, string storedName)
    {
        try
        {
            using var session = await repository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            var stored = await ExistsAsync(repository, session, storedName).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
            return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Verifying Data Protection key '{StoredName}' failed ({Message})", storedName,
                ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     False while the system tenant has no <c>System.Identity</c> construction kit (a fresh installation before
    ///     the identity service bootstrapped it): nothing can be read or written then.
    /// </summary>
    private async Task<bool> IsKeyTypeAvailableAsync(ITenantRepository repository)
    {
        try
        {
            using var session = await repository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            await repository.CountRtEntitiesByTypeAsync(session, KeyCkTypeId, RtEntityQueryOptions.Create())
                .ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
            return true;
        }
        catch (CkCacheException)
        {
            logger.LogWarning(
                "Data Protection keys cannot be stored yet: the system tenant has no {CkTypeId} (the identity service " +
                "installs it)", DataProtectionKeyXml.KeyCkTypeId);
            return false;
        }
    }

    private static async Task<bool> ExistsAsync(ITenantRepository repository, IOctoSession session, string storedName)
    {
        var existing = await repository.GetRtEntitiesByTypeAsync(session, KeyCkTypeId,
            RtEntityQueryOptions.Create()
                .FieldFilter(DataProtectionKeyXml.FriendlyNameAttributeName, FieldFilterOperator.Equals, storedName))
            .ConfigureAwait(false);
        return existing.Items.Any();
    }
}
