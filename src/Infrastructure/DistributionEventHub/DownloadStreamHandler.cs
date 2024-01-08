using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using MongoDB.Bson;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class DownloadStreamHandler : IDownloadStreamHandler
{
    private readonly Runtime.Contracts.MongoDb.Repository.IDownloadStreamHandler _streamHandler;

    public DownloadStreamHandler(Runtime.Contracts.MongoDb.Repository.IDownloadStreamHandler streamHandler)
    {
        _streamHandler = streamHandler;
    }

    public void Dispose()
    {
        _streamHandler.Dispose();
    }

    public ObjectId Id => ObjectId.Parse(_streamHandler.Id.ToString());
    public string ContentType => _streamHandler.ContentType;
    public DateTime UploadDateTime => _streamHandler.UploadDateTime;
    public Stream Stream => _streamHandler.Stream;
    public string Filename => _streamHandler.Filename;

    public void Close()
    {
        _streamHandler.Close(CancellationToken.None);
    }
}