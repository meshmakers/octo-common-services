using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using MongoDB.Bson;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class DownloadInfo(Runtime.Contracts.MongoDb.Repositories.IDownloadInfo downloadInfo)
    : IDownloadInfo
{
    public string ContentType => downloadInfo.ContentType;
    public string BinaryId => new (downloadInfo.BinaryId.ToString());
    public string Filename => downloadInfo.Filename;
    public DateTime UploadDateTime => downloadInfo.UploadDateTime;
    public long Length => downloadInfo.Length;
}