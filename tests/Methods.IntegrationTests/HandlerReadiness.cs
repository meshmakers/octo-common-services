using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     Readiness waits and failure descriptions for the bus tests. A test sends its first call only once the gateway's
///     handler registry lists every handler instance the test started, so each test starts from a known registry state
///     instead of racing the handlers' answers to the gateway's discovery request. A failed call is described with its
///     error code and parameters and the registry entries (source, withdrawn, expiry, no queue bound), so a failure on a
///     build agent names the dispatcher path it took.
/// </summary>
internal static class HandlerReadiness
{
    /// <summary>
    ///     Upper bound of a readiness wait. Generous for shared build agents; a wait that holds ends within milliseconds
    ///     on a quiet machine.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Waits until the registry of <paramref name="gateway" /> lists exactly <paramref name="instances" /> live handler
    ///     instances of <paramref name="methodId" />; fails the test with the registry content otherwise.
    /// </summary>
    public static async Task WaitForLiveHandlersAsync(IServiceProvider gateway, string methodId, int instances)
    {
        var registry = gateway.GetRequiredService<ICkMethodHandlerRegistry>();
        var deadline = DateTime.UtcNow + Timeout;
        while (registry.GetLiveHandlers(methodId).Count != instances && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var live = registry.GetLiveHandlers(methodId);
        Assert.True(live.Count == instances,
            $"The gateway registry lists {live.Count} live handler(s) of {methodId} after {Timeout}, expected {instances}: " +
            DescribeRegistry(gateway));
    }

    /// <summary>Every entry of the gateway's handler registry, for assertion messages.</summary>
    public static string DescribeRegistry(IServiceProvider gateway) =>
        string.Join("; ", gateway.GetRequiredService<ICkMethodHandlerRegistry>().Snapshot().Select(e =>
            $"{e.InstanceId} source={e.Source} withdrawn={e.Withdrawn} lastSeen={e.LastSeenUtc:O} expires={e.ExpiresUtc:O} " +
            $"noQueueBound={e.NoQueueBound}"));

    /// <summary>The error of a failed response and the gateway's registry entries, for assertion messages.</summary>
    public static string DescribeFailure(CkMethodResponse response, IServiceProvider gateway) =>
        $"{Describe(response)}; registry: {DescribeRegistry(gateway)}";

    /// <summary>The error of a failed response with its parameters (reason, method), for assertion messages.</summary>
    public static string Describe(CkMethodResponse response) =>
        response.Success
            ? "success"
            : $"{response.Error?.Code} [{string.Join(", ", (response.Error?.Parameters ?? new Dictionary<string, string>()).Select(p => $"{p.Key}={p.Value}"))}]";
}
