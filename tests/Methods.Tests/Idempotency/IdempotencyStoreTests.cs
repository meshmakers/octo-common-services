using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Idempotency;

/// <summary>AB#7817: the idempotency store contract, its in-memory implementation and its lifetime options.</summary>
public class IdempotencyStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLogCollector _logs = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodInMemoryIdempotencyStore Store(TimeSpan? ttl = null)
    {
        var lifetime = new CkMethodIdempotencyLifetime(
            Options.Create(new CkMethodIdempotencyOptions { Ttl = ttl }),
            LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs))).CreateLogger<CkMethodIdempotencyLifetime>());
        return new CkMethodInMemoryIdempotencyStore(lifetime, _time);
    }

    private static CkMethodIdempotencyKey Key(string key = "key-1", string subject = "user-1") =>
        CkMethodIdempotencyKey.Create("meshtest", "Sample.Accounts/Account.ChangePassword-1", subject, key);

    private CkMethodIdempotencyClaimRequest Claim(CkMethodIdempotencyKey key, Guid? invocation = null) =>
        new(key, invocation ?? Guid.NewGuid(), Now.AddSeconds(30), new byte[] { 1, 2, 3 }, "fp-key-1");

    // ---- claim -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task HundredConcurrentClaims_OfOneKey_ExactlyOneIsClaimed_AndNinetyNineSeeTheExistingRecord()
    {
        var store = Store();
        var key = Key();
        var requests = Enumerable.Range(0, 100).Select(_ => Claim(key)).ToArray();
        using var start = new ManualResetEventSlim();

        var tasks = requests.Select(r => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return store.ClaimAsync(r, TestContext.Current.CancellationToken);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        var winner = Assert.Single(results, r => r.Claimed);
        var others = results.Where(r => !r.Claimed).ToList();
        Assert.Equal(99, others.Count);
        Assert.All(others, o =>
        {
            Assert.Same(winner.Record, o.Record);
            Assert.Equal(winner.Record.FirstInvocationId, o.Record.FirstInvocationId);
        });
        Assert.Contains(requests, r => r.FirstInvocationId == winner.Record.FirstInvocationId);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Claim_StoresTheFactsOfTheFirstCall_AndNotTheRawKey()
    {
        var store = Store();
        var key = Key("a-very-secret-looking-key");
        var request = Claim(key);

        var result = await store.ClaimAsync(request, TestContext.Current.CancellationToken);

        var record = result.Record;
        Assert.True(result.Claimed);
        Assert.Equal(key.Id, record.Id);
        Assert.Equal(CkMethodIdempotencyState.InProgress, record.State);
        Assert.Equal(key.MethodId, record.MethodId);
        Assert.Equal(request.FirstInvocationId, record.FirstInvocationId);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.Fingerprint.ToArray());
        Assert.Equal("fp-key-1", record.FingerprintKeyId);
        Assert.Equal(Now, record.ClaimedAt);
        Assert.Equal(request.LeaseUntil, record.LeaseUntil);
        Assert.Equal(Now.AddHours(24), record.ExpiresAt);
        Assert.Null(record.Response);
        // the raw key and the fingerprint are in no printed form
        Assert.DoesNotContain("secret-looking", record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-looking", key.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-looking", key.Id, StringComparison.Ordinal);
        Assert.DoesNotContain("1, 2, 3", record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("01-02-03", record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("010203", record.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1, 2, 3", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredRecord_IsTakenOverByANewClaim_AndAReaderTreatsItAsAbsent()
    {
        var store = Store();
        var key = Key();
        var first = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        Assert.True(await store.CompleteAsync(key.Id, first.Record.FirstInvocationId, CkMethodIdempotencyState.Completed,
            CkMethodResponse.Succeeded(first.Record.FirstInvocationId, "h"), TestContext.Current.CancellationToken));

        _time.Advance(TimeSpan.FromHours(23));
        var live = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        Assert.False(live.Claimed); // still inside its lifetime: the stored first result
        Assert.Equal(CkMethodIdempotencyState.Completed, live.Record.State);
        Assert.False(live.Record.IsExpired(Now));

        _time.Advance(TimeSpan.FromHours(1)); // exactly ExpiresAt: expired
        Assert.True(first.Record.IsExpired(Now));
        var second = Claim(key);
        var taken = await store.ClaimAsync(second, TestContext.Current.CancellationToken);

        Assert.True(taken.Claimed);
        Assert.Equal(second.FirstInvocationId, taken.Record.FirstInvocationId);
        Assert.Equal(CkMethodIdempotencyState.InProgress, taken.Record.State);
        Assert.Null(taken.Record.Response);
        Assert.Equal(Now.AddHours(24), taken.Record.ExpiresAt); // a new lifetime from the new claim
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task ConcurrentTakeoverOfAnExpiredRecord_HasExactlyOneWinner()
    {
        var store = Store();
        var key = Key();
        await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(25));
        var requests = Enumerable.Range(0, 50).Select(_ => Claim(key)).ToArray();
        using var start = new ManualResetEventSlim();

        var tasks = requests.Select(r => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return store.ClaimAsync(r, TestContext.Current.CancellationToken);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        var winner = Assert.Single(results, r => r.Claimed);
        Assert.All(results.Where(r => !r.Claimed), o => Assert.Same(winner.Record, o.Record));
    }

    [Fact]
    public async Task ExpiredRecordsOfOtherKeys_AreSweptWhileClaimsArrive()
    {
        var store = Store();
        for (var i = 0; i < 10; i++)
        {
            await store.ClaimAsync(Claim(Key($"old-{i}")), TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromDays(2));
        for (var i = 0; i < 300; i++)
        {
            await store.ClaimAsync(Claim(Key($"new-{i}")), TestContext.Current.CancellationToken);
        }

        Assert.Equal(300, store.Count);
    }

    // ---- complete / release (row 4) --------------------------------------------------------------------------

    [Fact]
    public async Task Complete_StoresTheEnvelope_OnlyForTheOwner_AndOnlyOnce_WithoutExtendingTheLifetime()
    {
        var store = Store();
        var key = Key();
        var claimed = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;
        var response = CkMethodResponse.Succeeded(claimed.FirstInvocationId, "h", "{\"ok\":true}");

        Assert.False(await store.CompleteAsync(key.Id, Guid.NewGuid(), CkMethodIdempotencyState.Completed, response,
            TestContext.Current.CancellationToken));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.CompleteAsync(key.Id, claimed.FirstInvocationId, CkMethodIdempotencyState.Completed, response,
            TestContext.Current.CancellationToken));
        Assert.False(await store.CompleteAsync(key.Id, claimed.FirstInvocationId, CkMethodIdempotencyState.Completed, response,
            TestContext.Current.CancellationToken));

        var existing = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;
        Assert.Equal(CkMethodIdempotencyState.Completed, existing.State);
        Assert.Same(response, existing.Response);
        Assert.Equal(claimed.ExpiresAt, existing.ExpiresAt);
    }

    [Fact]
    public async Task UnknownOutcome_BurnsTheKey_NoResponseIsStored_AndAFurtherClaimSeesIt()
    {
        var store = Store();
        var key = Key();
        var claimed = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;

        Assert.True(await store.CompleteAsync(key.Id, claimed.FirstInvocationId, CkMethodIdempotencyState.Unknown,
            CkMethodResponse.Succeeded(claimed.FirstInvocationId, "h"), TestContext.Current.CancellationToken));

        var again = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        Assert.False(again.Claimed);
        Assert.Equal(CkMethodIdempotencyState.Unknown, again.Record.State);
        Assert.Null(again.Record.Response);
    }

    [Fact]
    public async Task HandlerFailure_StoredAsCompleted_BurnsTheKeyToo()
    {
        // row 4 (Odin): METHOD_HANDLER_FAILED burns the key, the caller retries with a new key
        var store = Store();
        var key = Key();
        var claimed = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;
        var failed = CkMethodResponse.Failed(claimed.FirstInvocationId, "h",
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed, (CkMethodErrorParameters.MethodId, key.MethodId)));
        await store.CompleteAsync(key.Id, claimed.FirstInvocationId, CkMethodIdempotencyState.Completed, failed,
            TestContext.Current.CancellationToken);

        var again = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);

        Assert.False(again.Claimed);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, again.Record.Response!.Error!.Code);
    }

    [Fact]
    public async Task Release_GivesTheKeyBack_OnlyToTheOwner_AndOnlyWhileInProgress()
    {
        var store = Store();
        var key = Key();
        var claimed = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;

        Assert.False(await store.ReleaseAsync(key.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.True(await store.ReleaseAsync(key.Id, claimed.FirstInvocationId, TestContext.Current.CancellationToken));
        Assert.False(await store.ReleaseAsync(key.Id, claimed.FirstInvocationId, TestContext.Current.CancellationToken));
        Assert.True((await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Claimed);

        // a settled record cannot be released
        var key2 = Key("key-2");
        var second = (await store.ClaimAsync(Claim(key2), TestContext.Current.CancellationToken)).Record;
        await store.CompleteAsync(key2.Id, second.FirstInvocationId, CkMethodIdempotencyState.Unknown, null,
            TestContext.Current.CancellationToken);
        Assert.False(await store.ReleaseAsync(key2.Id, second.FirstInvocationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Complete_RejectsInProgress_AndCompletedWithoutAResponse()
    {
        var store = Store();
        var key = Key();
        var claimed = (await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken)).Record;

        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(key.Id, claimed.FirstInvocationId,
            CkMethodIdempotencyState.InProgress, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(key.Id, claimed.FirstInvocationId,
            CkMethodIdempotencyState.Completed, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InFlightClaim_PastItsLease_ReadsAsUnknown()
    {
        var store = Store();
        var key = Key();
        var request = Claim(key); // lease 30 s
        var claimed = (await store.ClaimAsync(request, TestContext.Current.CancellationToken)).Record;

        Assert.Equal(CkMethodIdempotencyState.InProgress, claimed.ResolveState(Now.AddSeconds(29)));
        Assert.Equal(CkMethodIdempotencyState.Unknown, claimed.ResolveState(Now.AddSeconds(30)));
        // the record is still the live record: a repeat does not take it over before the lifetime ends
        _time.Advance(TimeSpan.FromMinutes(5));
        var again = await store.ClaimAsync(Claim(key), TestContext.Current.CancellationToken);
        Assert.False(again.Claimed);
        Assert.Equal(CkMethodIdempotencyState.Unknown, again.Record.ResolveState(Now));
    }

    // ---- key identity ----------------------------------------------------------------------------------------

    [Fact]
    public void KeyId_IsASha256Hex_AndEveryPartIsPartOfTheIdentity()
    {
        var id = CkMethodIdempotencyKey.Create("t", "m", "s", "k").Id;
        Assert.Matches("^[0-9a-f]{64}$", id);
        Assert.Equal(id, CkMethodIdempotencyKey.Create("t", "m", "s", "k").Id);
        Assert.NotEqual(id, CkMethodIdempotencyKey.Create("t2", "m", "s", "k").Id);
        Assert.NotEqual(id, CkMethodIdempotencyKey.Create("t", "m2", "s", "k").Id);
        Assert.NotEqual(id, CkMethodIdempotencyKey.Create("t", "m", "s2", "k").Id);
        Assert.NotEqual(id, CkMethodIdempotencyKey.Create("t", "m", "s", "k2").Id);
        // a value cannot shift into its neighbour
        Assert.NotEqual(CkMethodIdempotencyKey.Create("ab", "c", "s", "k").Id, CkMethodIdempotencyKey.Create("a", "bc", "s", "k").Id);
    }

    [Fact]
    public async Task TwoCallers_WithTheSameKey_DoNotShareARecord()
    {
        var store = Store();

        var alice = await store.ClaimAsync(Claim(Key(subject: "alice")), TestContext.Current.CancellationToken);
        var bob = await store.ClaimAsync(Claim(Key(subject: "bob")), TestContext.Current.CancellationToken);

        Assert.True(alice.Claimed && bob.Claimed);
    }

    [Theory]
    [InlineData("", "m", "s", "k")]
    [InlineData("t", " ", "s", "k")]
    [InlineData("t", "m", "", "k")]
    [InlineData("t", "m", "s", "")]
    public void Key_RejectsEmptyParts(string tenant, string method, string subject, string key) =>
        Assert.Throws<ArgumentException>(() => CkMethodIdempotencyKey.Create(tenant, method, subject, key));

    [Fact]
    public void Key_AcceptsExactly128Characters()
    {
        Assert.NotNull(CkMethodIdempotencyKey.Create("t", "m", "s", new string('k', 128)));
        Assert.Throws<ArgumentException>(() => CkMethodIdempotencyKey.Create("t", "m", "s", new string('k', 129)));
    }

    // ---- ttl options -----------------------------------------------------------------------------------------

    [Fact]
    public void Ttl_DefaultsTo24Hours_AndKeepsAValueInsideTheRange()
    {
        Assert.Equal(TimeSpan.FromHours(24), Lifetime(null).Ttl);
        Assert.Equal(TimeSpan.FromHours(1), Lifetime(TimeSpan.FromHours(1)).Ttl);
        Assert.Equal(TimeSpan.FromDays(7), Lifetime(TimeSpan.FromDays(7)).Ttl);
        Assert.Empty(_logs.GetSnapshot());
    }

    [Fact]
    public void Ttl_EightDays_IsClampedToSevenDays_WithOneWarning()
    {
        var lifetime = Lifetime(TimeSpan.FromDays(8));

        Assert.Equal(TimeSpan.FromDays(7), lifetime.Ttl);
        var warning = Assert.Single(_logs.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("clamped", warning.Message, StringComparison.Ordinal);
        _ = lifetime.Ttl; // reading again does not warn again
        Assert.Single(_logs.GetSnapshot());
    }

    private CkMethodIdempotencyLifetime Lifetime(TimeSpan? ttl) =>
        new(Options.Create(new CkMethodIdempotencyOptions { Ttl = ttl }),
            LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs))).CreateLogger<CkMethodIdempotencyLifetime>());

    [Theory]
    [InlineData("0:00:00")]
    [InlineData("-1:00:00")]
    public async Task Ttl_ZeroOrNegative_FailsTheStartOfTheService(string ttl)
    {
        using var host = new HostBuilder()
            .ConfigureServices(s => s.AddCkMethodIdempotencyOptions(o => o.Ttl = TimeSpan.Parse(ttl)))
            .Build();

        var e = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Methods:Idempotency:Ttl", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_BindsTheSection_AndResolvesTheInMemoryStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Methods:Idempotency:Ttl"] = "2.00:00:00" }).Build();
        using var host = new HostBuilder()
            .ConfigureServices(s => s.AddSingleton<IConfiguration>(config)
                .AddLogging().AddCkMethodInMemoryIdempotencyStore())
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.IsType<CkMethodInMemoryIdempotencyStore>(host.Services.GetRequiredService<ICkMethodIdempotencyStore>());
        Assert.Equal(TimeSpan.FromDays(2), host.Services.GetRequiredService<CkMethodIdempotencyLifetime>().Ttl);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Registration_KeepsAStoreThatWasRegisteredFirst()
    {
        var own = new CkMethodInMemoryIdempotencyStore(Lifetime(null), _time);
        using var host = new HostBuilder()
            .ConfigureServices(s => s.AddLogging().AddSingleton<ICkMethodIdempotencyStore>(own)
                .AddCkMethodInMemoryIdempotencyStore())
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Same(own, host.Services.GetRequiredService<ICkMethodIdempotencyStore>());
    }
}
