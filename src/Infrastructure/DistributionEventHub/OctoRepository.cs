using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using MongoDB.Bson;
using IDownloadInfo = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadInfo;
using IDownloadStreamHandler = Meshmakers.Octo.Common.DistributionEventHub.Repository.IDownloadStreamHandler;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class OctoRepository(ITenantRepository tenantRepository) : IRepository
{
    public IRepositoryCollection<TKey, TDocument> GetCollection<TKey, TDocument>(string? suffix = null)
        where TKey : notnull where TDocument : class, new()
    {
        // TODO: This has to be implemented to support sagas. Maybe we need for that extensions to bot construction kit.
        throw new NotImplementedException();
    }

    public async Task<string> UploadBinaryAsync(Stream stream, string contentType, string fileName, DateTime? expiry,
        CancellationToken cancellationToken = new())
    {
        using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var id = await tenantRepository
            .UploadTemporaryLargeBinaryAsync(session, fileName, contentType, expiry ?? DateTime.UtcNow.AddHours(1), stream, cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return id.ToString();
    }

    public async Task<string> UploadWithReplaceByFileNameBinaryAsync(Stream stream, string contentType, string fileName,
        CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var id = await tenantRepository.ReplaceTemporaryLargeBinaryAsync(session, fileName, contentType,
            stream, cancellationToken).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return id.ToString();
    }

    public async Task DeleteBinaryAsync(string cacheStreamKey, CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        await tenantRepository
            .DeleteTemporaryLargeBinaryAsync(session, new OctoObjectId(cacheStreamKey), cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);
    }

    public async Task DeleteAllBinariesWithExpiryAsync(CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        await tenantRepository
            .DeleteAllTemporaryLargeBinariesAsync(session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAllExpiredBinariesAsync(DateTime expiry, CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        await tenantRepository
            .DeleteExpiredTemporaryLargeBinariesAsync(session, expiry, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IDownloadInfo?> GetBinaryByIdAsync(string cacheStreamKey,
        CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var result = await tenantRepository
            .GetTemporaryLargeBinaryAsync(session, new OctoObjectId(cacheStreamKey), cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        if (result == null)
        {
            return null;
        }

        return new DownloadInfo(result);
    }

    public async Task<IDownloadInfo?> GetBinaryByFileNameAsync(string fileName,
        CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var result = await tenantRepository.GetTemporaryLargeBinaryAsync(session, fileName, cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        if (result == null)
        {
            return null;
        }

        return new DownloadInfo(result);
    }

    public async Task<IDownloadStreamHandler?> DownloadBinaryAsync(ObjectId id,
        CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var result = await tenantRepository
            .DownloadLargeBinaryAsync(session, new OctoObjectId(id.ToString()), cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return new DownloadStreamHandler(result);
    }

    public async Task<IDownloadStreamHandler?> DownloadBinaryAsync(string cacheStreamKey,
        CancellationToken cancellationToken = new())
    {
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var result = await tenantRepository
            .DownloadLargeBinaryAsync(session, new OctoObjectId(cacheStreamKey), cancellationToken)
            .ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return new DownloadStreamHandler(result);
    }
}