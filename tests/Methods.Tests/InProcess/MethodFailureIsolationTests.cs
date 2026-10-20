using System.Collections.Concurrent;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>
///     Rule 2 of the method consistency model: each method call is its own unit of work. A failing method call never
///     rolls back what the request committed earlier, and a failing earlier write never prevents an independent method
///     call. The "database" is a small store with explicit units of work; the request and the handler each use their own.
/// </summary>
public class MethodFailureIsolationTests
{
    private const string Declared = "PASSWORD_POLICY_VIOLATION";

    /// <summary>Committed documents plus units of work that publish their staged writes only on <see cref="Unit.Commit" />.</summary>
    private sealed class Store
    {
        private readonly ConcurrentDictionary<string, string> _committed = new();

        public IReadOnlyDictionary<string, string> Committed => _committed;

        public Unit Begin() => new(_committed);

        public sealed class Unit(ConcurrentDictionary<string, string> committed) : IDisposable
        {
            private readonly Dictionary<string, string> _staged = new();

            public void Stage(string key, string value) => _staged[key] = value;

            public void Commit()
            {
                foreach (var (key, value) in _staged)
                {
                    committed[key] = value;
                }

                _staged.Clear();
            }

            // Disposing a unit that was not committed discards its writes (rollback).
            public void Dispose() => _staged.Clear();
        }
    }

    private static InProcessFixture Fixture(Store store,
        Func<Store, CkMethodContext<UserChangePasswordParameters>, Task<CkMethodOutcome<UserChangePasswordResult>>> run) =>
        InProcessFixture.With(new ChangePasswordHandler((context, _) => run(store, context)),
            TestData.Definition(errors: Declared));

    private static async Task<CkMethodResponse> Call(InProcessFixture fixture) =>
        await fixture.Transport.InvokeAsync(TestData.Request(fixture.InSeconds(15)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFailingMethodCall_LeavesEarlierCommittedWritesOfTheRequestUntouched()
    {
        var store = new Store();
        // The request committed a write before it called the method.
        using (var request = store.Begin())
        {
            request.Stage("order-1", "paid");
            request.Commit();
        }

        // The method stages its own write and then fails: its unit of work is rolled back, nothing else.
        var failing = Fixture(store, async (s, _) =>
        {
            using var own = s.Begin();
            own.Stage("password-hash", "new");
            await Task.Yield();
            throw new InvalidOperationException("handler broke before its commit");
        });

        var response = await Call(failing);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal("paid", store.Committed["order-1"]);
        Assert.DoesNotContain("password-hash", store.Committed.Keys);
        Assert.Single(store.Committed);
    }

    [Fact]
    public async Task ADeclaredErrorOfTheMethod_AlsoLeavesEarlierCommittedWritesUntouched()
    {
        var store = new Store();
        using (var request = store.Begin())
        {
            request.Stage("order-1", "paid");
            request.Commit();
        }

        var refusing = Fixture(store, (_, _) =>
            Task.FromResult(CkMethodOutcome<UserChangePasswordResult>.Fail(Declared)));

        var response = await Call(refusing);

        Assert.Equal(Declared, response.Error!.Code);
        Assert.Equal("paid", store.Committed["order-1"]);
    }

    [Fact]
    public async Task AFailingEarlierWrite_DoesNotPreventAnIndependentMethodCall()
    {
        var store = new Store();
        // The earlier write of the request fails and is rolled back.
        try
        {
            using var request = store.Begin();
            request.Stage("order-2", "half-written");
            throw new InvalidOperationException("request write failed");
        }
        catch (InvalidOperationException)
        {
            // expected: the unit is disposed without Commit
        }

        var independent = Fixture(store, (s, _) =>
        {
            using var own = s.Begin();
            own.Stage("password-hash", "new");
            own.Commit();
            return Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
                new UserChangePasswordResult { Value = "changed" });
        });

        var response = await Call(independent);

        Assert.True(response.Success);
        Assert.Equal("new", store.Committed["password-hash"]);
        Assert.DoesNotContain("order-2", store.Committed.Keys);
    }

    [Fact]
    public async Task AFailedCall_IsNotRolledBackByALaterSuccessfulCall_AndTheOtherWayAround()
    {
        var store = new Store();
        var first = Fixture(store, (s, _) =>
        {
            using var own = s.Begin();
            own.Stage("a", "1");
            own.Commit();
            return Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
                new UserChangePasswordResult { Value = "changed" });
        });
        var second = Fixture(store, (s, _) =>
        {
            using var own = s.Begin();
            own.Stage("b", "2");
            throw new InvalidOperationException("second call fails");
        });

        Assert.True((await Call(first)).Success);
        Assert.False((await Call(second)).Success);

        Assert.Equal("1", store.Committed["a"]);
        Assert.DoesNotContain("b", store.Committed.Keys);
    }
}
