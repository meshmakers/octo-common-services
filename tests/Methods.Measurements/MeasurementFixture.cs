using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Meshmakers.Octo.Services.Methods.Measurements;

/// <summary>
///     Private containers for the measurements: a RabbitMQ broker (with the management plugin) and a single-node MongoDB
///     replica set. Nothing starts unless <c>OCTO_METHOD_MEASURE=1</c>, so the default test run stays free of containers.
///     Both containers are removed when the fixture is disposed (Testcontainers' resource reaper removes them even if the
///     test process dies).
/// </summary>
public sealed class MeasurementFixture : IAsyncLifetime
{
    public const string EnableVariable = "OCTO_METHOD_MEASURE";

    // Exact patch versions, never a floating tag (SERVER-121912 on newer kernels for Mongo; reproducible numbers).
    private const string MongoImage = "mongo:8.0.15";
    private const string RabbitImage = "rabbitmq:3-management";

    private RabbitMqContainer? _rabbit;
    private MongoDbContainer? _mongo;

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public const string SkipReason = $"Measurement, not a test: set {EnableVariable}=1 (see docs/method-performance.md, Measurements AB#7823).";

    public string RabbitHost => _rabbit!.Hostname;
    public int RabbitPort => _rabbit!.GetMappedPublicPort(5672);
    public int RabbitManagementPort => _rabbit!.GetMappedPublicPort(15672);
    public string MongoConnectionString => _mongo!.GetConnectionString();

    public string RabbitImageName => RabbitImage;
    public string MongoImageName => MongoImage;

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _rabbit = new RabbitMqBuilder(RabbitImage).WithUsername("guest").WithPassword("guest").WithPortBinding(15672, true).Build();
        _mongo = new MongoDbBuilder(MongoImage).WithReplicaSet().Build();
        await Task.WhenAll(_rabbit.StartAsync(), _mongo.StartAsync());
    }

    /// <summary>Runs <c>rabbitmqctl -q ...</c> in the private broker container.</summary>
    public async Task<string[]> RabbitMqCtlAsync(params string[] arguments)
    {
        var result = await _rabbit!.ExecAsync(["rabbitmqctl", "-q", .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)} failed: {result.Stderr}");
        }

        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async ValueTask DisposeAsync()
    {
        if (_rabbit != null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_mongo != null)
        {
            await _mongo.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MeasurementCollection : ICollectionFixture<MeasurementFixture>
{
    public const string Name = "Measurement";
}
