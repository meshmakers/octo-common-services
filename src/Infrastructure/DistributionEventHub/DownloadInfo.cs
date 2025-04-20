using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

internal class DownloadInfo(IBinaryInfo binaryInfo)
    : IDownloadInfo
{
    public string ContentType => binaryInfo.ContentType;
    public string BinaryId => new (binaryInfo.BinaryId.ToString());
    public string Filename => binaryInfo.Filename;
    public DateTime UploadDateTime => binaryInfo.UploadDateTime;
    public long Length => binaryInfo.Size;
}