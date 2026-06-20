using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure.Cors;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

internal class KnownOriginsProvider(ISystemContext systemContext) : IKnownOriginsProvider
{
    private static readonly RtCkId<CkTypeId> CkIdClient = new("System.Identity/Client");
    private const string AllowedCorsOriginsAttribute = "AllowedCorsOrigins";
    private const string ClientUriEntryUriAttribute = "Uri";

    private static async Task<IEnumerable<RtEntity>> GetClients(ITenantRepository tenantRepository)
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, CkIdClient, queryOptions).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);
        return result.Items;
    }

    public async Task<IReadOnlyCollection<string>> GetKnownOriginsAsync(string tenantId)
    {
        var tenantRepository = await systemContext.TryFindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        if (tenantRepository == null)
        {
            // Tenant not found, fall back to system tenant
            tenantRepository = systemContext.GetSystemTenantRepository();
        }

        var clients = await GetClients(tenantRepository).ConfigureAwait(false);
        var origins = clients.SelectMany(ExtractAllowedCorsOrigins);
        return new List<string>(origins);
    }

    /// <summary>
    ///     Reads the AllowedCorsOrigins attribute from an Identity Client entity, tolerating both
    ///     the pre-AB#4209 StringArray shape and the post-AB#4209 RecordArray&lt;ClientUriEntry&gt;
    ///     shape. Needed because every backend service shares this provider but Identity is the
    ///     only repo that gets the typed RtClient regeneration at the 2.9.0 schema bump — the other
    ///     services read the raw RtEntity from MongoDB and never see the typed property.
    /// </summary>
    private static IEnumerable<string> ExtractAllowedCorsOrigins(RtEntity client)
    {
        if (!client.Attributes.TryGetValue(AllowedCorsOriginsAttribute, out var raw) || raw is null)
        {
            return [];
        }

        // Post-2.9.0: RecordArray of ClientUriEntry. Read the Uri attribute on each record.
        if (raw is IEnumerable<RtRecord> records)
        {
            return records
                .Select(r => r.GetAttributeStringValueOrDefault(ClientUriEntryUriAttribute))
                .Where(s => !string.IsNullOrEmpty(s))!
                .Cast<string>();
        }

        // Pre-2.9.0 legacy shape: StringArray. Some MongoDB deserialization paths surface this as
        // IEnumerable<object> — handle both, projecting strings and records uniformly.
        if (raw is IEnumerable<object> items)
        {
            return items
                .Select(item => item switch
                {
                    string s => s,
                    RtRecord r => r.GetAttributeStringValueOrDefault(ClientUriEntryUriAttribute) ?? string.Empty,
                    _ => string.Empty
                })
                .Where(s => !string.IsNullOrEmpty(s));
        }

        return [];
    }
}