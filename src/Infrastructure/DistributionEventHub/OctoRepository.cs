using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using IDownloadInfo = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadInfo;
using IDownloadStreamHandler = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadStreamHandler;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class OctoRepository : IRepository
{
    private readonly ITenantRepository _tenantRepository;

    public OctoRepository(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }
    
    public IRepositoryCollection<TKey, TDocument> GetCollection<TKey, TDocument>(string? suffix = null) 
        where TKey : notnull where TDocument : class, new()
    {
        // TODO: This has to be implemented to support sagas. Maybe we need for that extensions to bot construction kit.
        throw new NotImplementedException();
    }

    public async Task<string> UploadBinaryAsync(Stream stream, string contentType, string fileName, DateTime? expiry,
        CancellationToken cancellationToken = new())
    {
        var id = await _tenantRepository.UploadLargeBinaryAsync(fileName, contentType, stream, cancellationToken);
        return id.ToString();
    }

    public async Task DeleteBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        await _tenantRepository.DeleteLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken);
    }

    public async Task<IDownloadInfo?> GetBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        var result = await _tenantRepository.GetLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken);
        return new DownloadInfo(result);
    }

    public async Task<IDownloadStreamHandler?> DownloadBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        var result = await _tenantRepository.DownloadLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken);
        return new DownloadStreamHandler(result);
    }
}