using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Operations;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>
///     AB#8443, OD-5 point 7: the same idempotency key twice for an asynchronous method returns the stored accepted envelope
///     with the same operation id, and only one operation exists (the second call never reaches the handler). End-to-end
///     through GraphQL is AB#8444.
/// </summary>
public class AsyncIdempotencyTests
{
    private static readonly string KeyMaterial = Convert.ToBase64String(Enumerable.Repeat((byte)0x44, 32).ToArray());

    [Fact]
    public async Task SameKeyTwice_ReturnsTheSameOperationId_AndOneOperationExists()
    {
        var handler = new ExportHandler((_, _) =>
            Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult { Value = "x" }));
        var fixture = AsyncOperationFixture.With(handler);
        var definition = AsyncOperationFixture.AsyncDefinition();
        var sensitivity = new DefinitionSensitivity(definition);
        var lifetime = new CkMethodIdempotencyLifetime(Options.Create(new CkMethodIdempotencyOptions()),
            NullLogger<CkMethodIdempotencyLifetime>.Instance);
        var idempotencyStore = new CkMethodInMemoryIdempotencyStore(lifetime, fixture.Time);
        var filter = new CkMethodIdempotencyFilter(idempotencyStore, new FixedPrincipal(HostFixture.Administrator),
            sensitivity, Options.Create(new CkMethodDispatcherOptions()), fixture.Time,
            NullLogger<CkMethodIdempotencyFilter>.Instance);
        var pipeline = new CkMethodInvocationPipeline([filter], sensitivity,
            NullLogger<CkMethodInvocationPipeline>.Instance,
            new CkMethodParametersFingerprinter(Options.Create(new CkMethodFingerprintOptions
            {
                Current = new() { Id = "k1", Secret = KeyMaterial }
            })));

        Task<CkMethodResponse> Call() =>
            pipeline.ExecuteAsync(CkMethodTransportNames.InProcess,
                fixture.Request() with { IdempotencyKey = "export-1" },
                r => fixture.Invoker.InvokeAsync(r, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

        var first = await Call();
        var second = await Call();

        Assert.True(first.IsAccepted, first.ToString());
        Assert.True(second.IsAccepted, second.ToString());
        Assert.Equal(first.Operation!.OperationId, second.Operation!.OperationId);
        Assert.NotEqual(first.InvocationId, second.InvocationId);
        Assert.Equal(1, handler.Accepts);
        await fixture.FinishedAsync(first);
        Assert.Equal(1, ((CkMethodInMemoryOperationStore)fixture.Store).Count);
        var page = await fixture.Store.ListAsync(new CkMethodOperationQuery { TenantId = "meshtest" },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, page.TotalCount);
    }

    private sealed class FixedPrincipal(CkMethodPrincipal principal) : ICkMethodPrincipalProvider
    {
        public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) => principal;
    }
}
