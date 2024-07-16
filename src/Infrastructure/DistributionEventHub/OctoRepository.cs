using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using MongoDB.Bson;
using IDownloadInfo = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadInfo;
using IDownloadStreamHandler = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadStreamHandler;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class OctoRepository(ITenantRepository tenantRepository) : IRepository
{
    private const string ExpiryDateTime = "expiryDateTime";

    public IRepositoryCollection<TKey, TDocument> GetCollection<TKey, TDocument>(string? suffix = null) 
        where TKey : notnull where TDocument : class, new()
    {
        // TODO: This has to be implemented to support sagas. Maybe we need for that extensions to bot construction kit.
        throw new NotImplementedException();
    }

    public async Task<string> UploadBinaryAsync(Stream stream, string contentType, string fileName, DateTime? expiry,
        CancellationToken cancellationToken = new())
    {
        var dictionary = new Dictionary<string, object>();
        if (expiry != null)
        {
            dictionary.Add(ExpiryDateTime, expiry);
        }
        var id = await tenantRepository.UploadLargeBinaryAsync(fileName, contentType, stream, dictionary, cancellationToken).ConfigureAwait(false);
        return id.ToString();
    }

    public async Task<string> UploadWithReplaceByFileNameBinaryAsync(Stream stream, string contentType, string fileName,
        CancellationToken cancellationToken = new())
    {
        var dictionary = new Dictionary<string, object>();
        var id = await tenantRepository.ReplaceLargeBinaryAsync(fileName, contentType,
            stream, dictionary, cancellationToken).ConfigureAwait(false);
        return id.ToString();
    }

    public async Task DeleteBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        await tenantRepository.DeleteLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IDownloadInfo?> GetBinaryByIdAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        var result = await tenantRepository.GetLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken)
            .ConfigureAwait(false);
        if (result == null)
        {
            return null;
        }
        return new DownloadInfo(result);
    }

    public async Task<IDownloadInfo?> GetBinaryByFileNameAsync(string fileName, CancellationToken cancellationToken = new())
    {
        var result = await tenantRepository.GetLargeBinaryAsync(fileName, cancellationToken)
            .ConfigureAwait(false);
        if (result == null)
        {
            return null;
        }
        return new DownloadInfo(result);
    }

    public async Task<IDownloadStreamHandler?> DownloadBinaryAsync(ObjectId id, CancellationToken cancellationToken = new())
    {
        var result = await tenantRepository.DownloadLargeBinaryAsync(new OctoObjectId(id.ToString()), cancellationToken)
            .ConfigureAwait(false);
        return new DownloadStreamHandler(result);
    }

    public async Task<IDownloadStreamHandler?> DownloadBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        var result = await tenantRepository.DownloadLargeBinaryAsync(new OctoObjectId(cacheStreamKey), cancellationToken)
            .ConfigureAwait(false);
        return new DownloadStreamHandler(result);
    }
}