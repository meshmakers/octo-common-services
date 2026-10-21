using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;

using MongoDB.Bson;

using Xunit;

namespace Infrastructure.Tests.DistributionEventHub;

/// <summary>
///     AB#7256: a cache file that does not exist is reported as <c>null</c> (the contract of
///     <c>IRepository.DownloadBinaryAsync</c>), not as the repository's <see cref="EntityNotFoundException" />, so that
///     the bot services can answer with a typed error that names tenant and key.
/// </summary>
public class OctoRepositoryDownloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    public OctoRepositoryDownloadTests()
    {
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task DownloadByKey_FileNotFound_ReturnsNullAndAbortsTheTransaction()
    {
        var key = ObjectId.GenerateNewId().ToString();
        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(_session, A<OctoObjectId>._, A<CancellationToken>._))
            .Throws(EntityNotFoundException.IdNotFound("IDownloadStreamHandler", key));

        var result = await new OctoRepository(_tenantRepository).DownloadBinaryAsync(key, Ct);

        Assert.Null(result);
        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task DownloadById_FileNotFound_ReturnsNull()
    {
        var id = ObjectId.GenerateNewId();
        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(_session, A<OctoObjectId>._, A<CancellationToken>._))
            .Throws(EntityNotFoundException.IdNotFound("IDownloadStreamHandler", id.ToString()));

        Assert.Null(await new OctoRepository(_tenantRepository).DownloadBinaryAsync(id, Ct));
    }

    [Fact]
    public async Task DownloadByKey_OtherErrors_AreNotSwallowed()
    {
        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(_session, A<OctoObjectId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OctoRepository(_tenantRepository).DownloadBinaryAsync(ObjectId.GenerateNewId().ToString(), Ct));
    }
}
