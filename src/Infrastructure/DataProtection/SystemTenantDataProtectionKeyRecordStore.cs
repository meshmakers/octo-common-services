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
///     written before the system tenant exists: a write into a system database the bootstrap has not created yet would
///     materialize a collection the bootstrap does not expect.
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
        return await scope.ServiceProvider.GetRequiredService<ISystemContext>().IsSystemTenantExistingAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataProtectionKeyRecord>> LoadAllAsync()
    {
        using var scope = serviceScopeFactory.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return [];
        }

        var repository = systemContext.GetSystemTenantRepositoryAsAdmin();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByTypeAsync(session, KeyCkTypeId, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();

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
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            throw new InvalidOperationException(
                $"Data Protection key '{record.StoredName}' cannot be persisted before the system tenant exists.");
        }

        var repository = systemContext.GetSystemTenantRepositoryAsAdmin();
        try
        {
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            if (await ExistsAsync(repository, session, record.StoredName))
            {
                await session.CommitTransactionAsync();
                return;
            }

            var entity = new RtEntity(KeyCkTypeId, OctoObjectId.GenerateNewId());
            entity.SetAttributeValue(DataProtectionKeyXml.FriendlyNameAttributeName, AttributeValueTypesDto.String,
                record.StoredName);
            entity.SetAttributeValue(DataProtectionKeyXml.XmlDataAttributeName, AttributeValueTypesDto.String,
                record.XmlData);
            entity.SetAttributeValue(DataProtectionKeyXml.CreationDateTimeAttributeName,
                AttributeValueTypesDto.DateTime, DateTime.UtcNow);
            await repository.InsertOneRtEntityAsync(session, KeyCkTypeId, entity);
            await session.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            // Unique index on the name: a concurrent replica won the race and the element IS stored, which is all
            // Data Protection needs. Anything else is a real error.
            logger.LogWarning("Storing Data Protection key '{StoredName}' hit a conflict ({Message}); verifying",
                record.StoredName, ex.Message);

            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            var stored = await ExistsAsync(repository, session, record.StoredName);
            await session.CommitTransactionAsync();
            if (!stored)
            {
                throw;
            }
        }
    }

    private static async Task<bool> ExistsAsync(ITenantRepository repository, IOctoSession session, string storedName)
    {
        var existing = await repository.GetRtEntitiesByTypeAsync(session, KeyCkTypeId,
            RtEntityQueryOptions.Create()
                .FieldFilter(DataProtectionKeyXml.FriendlyNameAttributeName, FieldFilterOperator.Equals, storedName));
        return existing.Items.Any();
    }
}
