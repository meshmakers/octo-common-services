using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Common.Cors;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

internal class KnownOriginsProvider(IMultiTenancyResolverService multiTenancyResolverService) : IKnownOriginsProvider
{
    private readonly ITenantRepository _tenantRepository = multiTenancyResolverService.GetTenantRepository();
    
    private static readonly CkId<CkTypeId> CkIdClient = new("System.Identity/Client"); 
    private const string AllowedCorsOriginsAttribute = "AllowedCorsOrigins";

    private async Task<IEnumerable<RtEntity>> GetClients()
    {
        var session = await _tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var dataQueryOperation = DataQueryOperation.Create();

        var result = await _tenantRepository.GetRtEntitiesByTypeAsync(session, CkIdClient, dataQueryOperation).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);
        return result.Items;
    }
    
    public async Task<IReadOnlyCollection<string>> GetKnownOriginsAsync(string tenantId)
    {
        var clients = await GetClients().ConfigureAwait(false);
        var origins = clients.SelectMany(x => x.GetAttributeStringValues(AllowedCorsOriginsAttribute));
        return new List<string>(origins);
    }
}