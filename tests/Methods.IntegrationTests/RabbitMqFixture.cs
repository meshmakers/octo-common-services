using Testcontainers.RabbitMq;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     Private RabbitMQ container per test run (no shared broker state, removed afterwards).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:3-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(5672);

    /// <summary>Runs <c>rabbitmqctl -q ...</c> in the private broker container and returns its output lines.</summary>
    public async Task<string[]> RabbitMqCtlAsync(params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "-q", .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)} failed: {result.Stderr}");
        }

        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}
