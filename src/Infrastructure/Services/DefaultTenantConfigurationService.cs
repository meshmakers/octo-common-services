using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public abstract class DefaultTenantConfigurationService(ISystemContext systemContext) : ITenantConfigurationService
{
    private readonly Dictionary<Tuple<string, string>, RtConfiguration> _configurations = new();

    protected async Task<T> GetOrRetrieveConfiguration<T>(string tenantId, string configurationName) where T: RtConfiguration, new()
    {
        var key = new Tuple<string, string>(tenantId, configurationName);
        if (_configurations.TryGetValue(key, out var configuration))
        {
            return (T) configuration;
        }
        
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);

        var session = tenantRepository.GetSession();
        try
        {
            session.StartTransaction();
            
            var dataQueryOperation = DataQueryOperation.Create()
                .FieldEquals(nameof(RtConfiguration.RtWellKnownName), configurationName);
            
            var resultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync<T>(session,
                    dataQueryOperation).ConfigureAwait(false);
            
            await session.CommitTransactionAsync().ConfigureAwait(false);
            
            var result = resultSet.Items.FirstOrDefault();
            if (result == null)
            {
                throw ConfigurationException.ConfigurationNotFound(tenantId, configurationName);
            }
            _configurations.Add(key, result);
            return result;
        }
        catch (Exception e)
        {
            throw ConfigurationException.UnableToRetrieveConfiguration(tenantId, configurationName, e);
        }

    } 
        
    public Task UpdateAsync(string tenantId, string configurationName)
    {
        if (_configurations.TryGetValue(new Tuple<string, string>(tenantId, configurationName), out _))
        {
            _configurations.Remove(new Tuple<string, string>(tenantId, configurationName));
        }
        return Task.CompletedTask;
    }
}