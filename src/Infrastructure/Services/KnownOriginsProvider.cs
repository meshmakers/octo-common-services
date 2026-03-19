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
        var origins = clients.SelectMany(x => x.GetAttributeStringValues(AllowedCorsOriginsAttribute));
        return new List<string>(origins);
    }
}