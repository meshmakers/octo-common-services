using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class OctoRepositoryClient : IRepositoryClient
{
    private readonly ISystemContext _systemContext;

    public OctoRepositoryClient(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }
    
    public async Task<IRepository> GetRepositoryAsync(string repositoryName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(repositoryName).ConfigureAwait(false);
        
        return new OctoRepository(tenantRepository);
    }

    public Task<IRepositorySession> StartSessionAsync()
    {
        throw new NotImplementedException();
    }

    public bool IsConnected { get; }
}