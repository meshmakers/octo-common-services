using FakeItEasy;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;
using Meshmakers.Octo.Services.ArtifactStorage.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ArtifactStorage.Tests;

public sealed class HealthCheckTests : IDisposable
{
    private readonly string _rootPath =
        Path.Combine(Path.GetTempPath(), "octo-artifact-health-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private static HealthCheckContext Context(IHealthCheck check)
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("artifact-storage", check, HealthStatus.Unhealthy, null)
        };
    }

    [Fact]
    public async Task WriteProbe_IsHealthyAndLeavesNothingBehind()
    {
        var store = new FileSystemArtifactStore(_rootPath);
        var check = new ArtifactStoreHealthCheck(store,
            Options.Create(new ArtifactStorageOptions { InstancePrefix = "main", HealthCheckWriteProbe = true }));

        var result = await check.CheckHealthAsync(Context(check), Ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("FileSystem", result.Data["provider"]);
        Assert.Empty(await ToListAsync(store.ListAsync("", Ct)));
    }

    [Fact]
    public async Task FailingStore_IsUnhealthy()
    {
        var store = A.Fake<IArtifactStore>();
        A.CallTo(() => store.ProviderName).Returns("S3");
        A.CallTo(() => store.ListAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("unreachable"));
        var check = new ArtifactStoreHealthCheck(store, Options.Create(new ArtifactStorageOptions()));

        var result = await check.CheckHealthAsync(Context(check), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task Registration_ResolvesThroughHealthCheckService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ArtifactStorage:FileSystem:RootPath"] = _rootPath,
            ["ArtifactStorage:HealthCheckWriteProbe"] = "true"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddArtifactStorage(configuration);
        services.AddHealthChecks().AddArtifactStorageHealthCheck();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);

        Assert.Equal(HealthStatus.Healthy, report.Entries[ArtifactStorageServiceCollectionExtensions.DefaultHealthCheckName].Status);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
