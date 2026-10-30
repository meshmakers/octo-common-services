using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>AB#7816: the per-invocation facts the pipeline filters share.</summary>
public class InvocationFactsTests
{
    private static readonly CkMethodParametersFingerprint Fingerprint =
        new([1, 2, 3, 4, 5, 6, 7, 8], "key-synthetic-1");

    /// <summary>Band 100: reads the facts after <c>next()</c> returned and keeps what it saw.</summary>
    private sealed class OuterFilter : ICkMethodInvocationFilter
    {
        public List<(Guid? ReplayOf, CkMethodInvocationFacts Facts)> Seen { get; } = [];

        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            var response = await next(context.Request);
            lock (Seen)
            {
                Seen.Add((context.Facts.ReplayOf, context.Facts));
            }

            return response;
        }
    }

    /// <summary>Band 300: marks the call as a replay of its own first invocation id and passes on.</summary>
    private sealed class InnerFilter(Func<CkMethodRequest, Guid?> replayOf, Func<Task>? gate = null)
        : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Idempotency;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            context.Facts.ReplayOf = replayOf(context.Request);
            if (gate != null)
            {
                await gate();
            }

            // `with` keeps the facts: the outer filter reads what was set here
            return await next(context.Request);
        }
    }

    [Fact]
    public async Task OuterFilter_ReadsReplayOfSetByTheInnerFilter_InProcessAndBus()
    {
        var first = Guid.NewGuid();
        var outer = new OuterFilter();
        var inner = new InnerFilter(_ => first);
        var busHost = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, outer, inner);

        var local = await inProcess.Transport.InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);
        var bus = await inProcess.Pipeline.ExecuteAsync(CkMethodTransportNames.Bus,
            TestData.Request(inProcess.InSeconds(15)), r => busHost.BusConsumer.HandleAsync(r),
            TestContext.Current.CancellationToken);

        Assert.True(local.Success && bus.Success);
        Assert.Equal([first, first], outer.Seen.Select(s => s.ReplayOf));
    }

    [Fact]
    public async Task WithoutAnInnerFilterSetting_ReplayOfIsNull()
    {
        var outer = new OuterFilter();
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, outer);

        await inProcess.Transport.InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(outer.Seen).ReplayOf);
    }

    [Fact]
    public async Task ConcurrentInvocations_EachHaveTheirOwnFacts()
    {
        var outer = new OuterFilter();
        var arrived = new TaskCompletionSource();
        var count = 0;
        // both calls sit inside the inner filter at the same time before either continues
        var inner = new InnerFilter(r => r.InvocationId, async () =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                arrived.SetResult();
            }

            await arrived.Task;
        });
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, outer, inner);
        var a = TestData.Request(inProcess.InSeconds(15)) with { InvocationId = Guid.NewGuid() };
        var b = TestData.Request(inProcess.InSeconds(15)) with { InvocationId = Guid.NewGuid() };

        await Task.WhenAll(
            inProcess.Transport.InvokeAsync(a, TestContext.Current.CancellationToken),
            inProcess.Transport.InvokeAsync(b, TestContext.Current.CancellationToken));

        Assert.Equal(2, outer.Seen.Count);
        Assert.NotSame(outer.Seen[0].Facts, outer.Seen[1].Facts);
        Assert.Equivalent(new[] { a.InvocationId, b.InvocationId }, outer.Seen.Select(s => s.ReplayOf!.Value));
    }

    [Fact]
    public async Task EveryFilterOfOneCall_SeesTheSameInstance()
    {
        var instances = new List<CkMethodInvocationFacts>();
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId,
            new Capture(CkMethodInvocationFilterOrder.Audit, instances),
            new Capture(CkMethodInvocationFilterOrder.Authorization, instances),
            new Capture(CkMethodInvocationFilterOrder.Idempotency, instances));

        await inProcess.Transport.InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, instances.Count);
        Assert.All(instances, i => Assert.Same(instances[0], i));
    }

    private sealed class Capture(int order, List<CkMethodInvocationFacts> into) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            into.Add(context.Facts);
            return next(context.Request);
        }
    }

    [Fact]
    public void Facts_AreNotPartOfTheWireContract()
    {
        // not a member of the request or the response envelope: nothing to serialize on the wire
        Assert.DoesNotContain(typeof(CkMethodRequest).GetProperties(), p => p.PropertyType == typeof(CkMethodInvocationFacts));
        Assert.DoesNotContain(typeof(CkMethodResponse).GetProperties(), p => p.PropertyType == typeof(CkMethodInvocationFacts));
        var wire = JsonSerializer.Serialize(TestData.Request(DateTime.UtcNow.AddSeconds(15)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("facts", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToString_OfTheFactsAndOfTheContext_PrintsNoFingerprintValue()
    {
        var facts = new CkMethodInvocationFacts { ReplayOf = Guid.NewGuid(), ParametersFingerprint = Fingerprint };
        var context = new CkMethodInvocationContext(CkMethodTransportNames.Bus,
            TestData.Request(DateTime.UtcNow.AddSeconds(15)), CancellationToken.None) { Facts = facts };

        foreach (var text in new[] { facts.ToString(), Fingerprint.ToString(), context.ToString() })
        {
            Assert.DoesNotContain(Convert.ToHexString(Fingerprint.ToArray()), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(Fingerprint.ToArray()), text, StringComparison.Ordinal);
            Assert.DoesNotContain("1, 2, 3", text, StringComparison.Ordinal);
        }

        Assert.Contains(facts.ReplayOf!.Value.ToString(), facts.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", facts.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_ComparesInConstantTime_AndNeverAcrossKeys()
    {
        Assert.True(Fingerprint.Matches(new CkMethodParametersFingerprint([1, 2, 3, 4, 5, 6, 7, 8], "key-synthetic-1")));
        Assert.False(Fingerprint.Matches(new CkMethodParametersFingerprint([1, 2, 3, 4, 5, 6, 7, 9], "key-synthetic-1")));
        Assert.False(Fingerprint.Matches(new CkMethodParametersFingerprint([1, 2, 3, 4, 5, 6, 7, 8], "key-synthetic-2")));
        Assert.False(Fingerprint.Matches(null));
        // the copy handed out cannot change the fingerprint
        Fingerprint.ToArray()[0] = 99;
        Assert.True(Fingerprint.Matches(new CkMethodParametersFingerprint([1, 2, 3, 4, 5, 6, 7, 8], "key-synthetic-1")));
    }
}
