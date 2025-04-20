using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using MongoDB.Bson;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class DownloadStreamHandler(Runtime.Contracts.Repositories.IDownloadStreamHandler streamHandler)
    : IDownloadStreamHandler
{
    public void Dispose()
    {
        streamHandler.Dispose();
    }

    public string Id => streamHandler.BinaryId.ToString();
    public string ContentType => streamHandler.ContentType;
    public DateTime UploadDateTime => streamHandler.UploadDateTime;
    public Stream Stream => streamHandler.Stream;
    public string Filename => streamHandler.Filename;

    public void Close()
    {
        streamHandler.Close(CancellationToken.None);
    }
}