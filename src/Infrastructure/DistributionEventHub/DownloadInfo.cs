using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using MongoDB.Bson;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class DownloadInfo : IDownloadInfo
{
    private readonly Runtime.Contracts.MongoDb.Repository.IDownloadInfo _downloadInfo;

    public DownloadInfo(Runtime.Contracts.MongoDb.Repository.IDownloadInfo downloadInfo)
    {
        _downloadInfo = downloadInfo;
    }

    public string ContentType => _downloadInfo.ContentType;
    public ObjectId BinaryId => new (_downloadInfo.BinaryId.ToString());
    public string Filename => _downloadInfo.Filename;
    public DateTime UploadDateTime => _downloadInfo.UploadDateTime;
    public long Length => _downloadInfo.Length;
}