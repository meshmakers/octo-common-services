using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#8177 — a MongoDB user that vanishes under a RUNNING tenant is re-created by whoever sees the
///     authentication failure, without a restart and at most once a minute per tenant.
/// </summary>
public class TenantDatabaseUserHealerTests
{
    private static readonly ConnectionId Connection = new(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017)));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    private static MongoCommandException CommandFailure(int code) =>
        new(Connection, "failed", new BsonDocument(), new BsonDocument { { "code", code } });

    [Theory]
    [InlineData(13)]
    [InlineData(18)]
    public void Unauthorized_and_AuthenticationFailed_command_errors_are_authentication_failures(int code) =>
        Assert.True(TenantDatabaseUserHealer.IsAuthenticationFailure(CommandFailure(code)));

    [Fact]
    public void A_failed_SCRAM_authentication_is_an_authentication_failure() =>
        Assert.True(TenantDatabaseUserHealer.IsAuthenticationFailure(new MongoAuthenticationException(Connection, "Unable to authenticate")));

    [Fact]
    public void The_failure_is_found_in_inner_and_aggregate_exceptions()
    {
        Assert.True(TenantDatabaseUserHealer.IsAuthenticationFailure(new InvalidOperationException("x", CommandFailure(13))));
        Assert.True(TenantDatabaseUserHealer.IsAuthenticationFailure(
            new AggregateException(new TimeoutException(), CommandFailure(18))));
    }

    [Theory]
    [InlineData(11000)]
    [InlineData(112)]
    public void Other_command_errors_are_not_authentication_failures(int code) =>
        Assert.False(TenantDatabaseUserHealer.IsAuthenticationFailure(CommandFailure(code)));

    [Fact]
    public void Plain_exceptions_and_null_are_not_authentication_failures()
    {
        Assert.False(TenantDatabaseUserHealer.IsAuthenticationFailure(new InvalidOperationException()));
        Assert.False(TenantDatabaseUserHealer.IsAuthenticationFailure(null));
    }

    [Fact]
    public async Task A_missing_user_is_recreated()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x"))
            .Returns(TenantDatabaseUserEnsureResult.Created);
        var sut = new TenantDatabaseUserHealer(_time);

        var result = await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance);

        Assert.Equal(TenantDatabaseUserEnsureResult.Created, result);
    }

    [Fact]
    public async Task Per_tenant_it_runs_at_most_once_per_interval_and_other_tenants_are_independent()
    {
        var sut = new TenantDatabaseUserHealer(_time);

        Assert.NotNull(await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance));
        Assert.Null(await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance));
        Assert.NotNull(await sut.TryHealAsync(_systemContext, "y", NullLogger.Instance));

        _time.Advance(sut.MinimumInterval + TimeSpan.FromSeconds(1));

        Assert.NotNull(await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance));
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).MustHaveHappenedTwiceExactly();
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("y")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task A_service_without_admin_access_does_not_throw_and_retries_after_the_interval()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).ThrowsAsync(CommandFailure(13));
        var sut = new TenantDatabaseUserHealer(_time);

        Assert.Null(await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance));

        _time.Advance(sut.MinimumInterval);
        Assert.Null(await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance));
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).MustHaveHappenedTwiceExactly();
    }

    [Theory]
    [InlineData(TenantDatabaseUserEnsureResult.AlreadyPresent)]
    [InlineData(TenantDatabaseUserEnsureResult.Created)]
    public async Task An_authentication_failure_evicts_the_pooled_clients_whatever_the_ensure_result(
        TenantDatabaseUserEnsureResult ensureResult)
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).Returns(ensureResult);
        var sut = new TenantDatabaseUserHealer(_time);

        var result = await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(13));

        Assert.Equal(ensureResult, result);
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("x", A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Eviction_respects_the_per_tenant_rate_limit()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x"))
            .Returns(TenantDatabaseUserEnsureResult.AlreadyPresent);
        var sut = new TenantDatabaseUserHealer(_time);

        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(13));
        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(13));
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("x", A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        _time.Advance(sut.MinimumInterval + TimeSpan.FromSeconds(1));
        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(18));
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("x", A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task A_non_authentication_cause_or_no_cause_never_evicts()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync(A<string>._))
            .Returns(TenantDatabaseUserEnsureResult.AlreadyPresent);
        var sut = new TenantDatabaseUserHealer(_time);

        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, new TimeoutException());
        await sut.TryHealAsync(_systemContext, "y", NullLogger.Instance, CommandFailure(11000));
        await sut.TryHealAsync(_systemContext, "z", NullLogger.Instance);

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task A_failing_eviction_does_not_throw_and_keeps_the_ensure_result()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x"))
            .Returns(TenantDatabaseUserEnsureResult.AlreadyPresent);
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("x", A<string?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = new TenantDatabaseUserHealer(_time);

        var result = await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(13));

        Assert.Equal(TenantDatabaseUserEnsureResult.AlreadyPresent, result);
    }

    [Fact]
    public async Task A_skipped_heal_does_not_evict()
    {
        var sut = new TenantDatabaseUserHealer(_time);
        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance);

        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(13));

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task EvictOnAuthenticationFailure_evicts_once_per_window_and_tenant()
    {
        var sut = new TenantDatabaseUserHealer(_time);
        var cause = CommandFailure(13);

        Assert.True(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, cause));
        Assert.False(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, cause));
        Assert.True(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "y", NullLogger.Instance, cause));
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync("x", null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        _time.Advance(sut.EvictionInterval + TimeSpan.FromSeconds(1));
        Assert.True(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, cause));
    }

    [Fact]
    public async Task EvictOnAuthenticationFailure_ignores_other_errors_and_missing_tenant()
    {
        var sut = new TenantDatabaseUserHealer(_time);

        Assert.False(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(11000)));
        Assert.False(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, new TimeoutException()));
        Assert.False(await sut.EvictOnAuthenticationFailureAsync(_systemContext, null, NullLogger.Instance, CommandFailure(13)));
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task EvictOnAuthenticationFailure_works_without_admin_access_and_never_throws()
    {
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .Throws<InvalidOperationException>();
        var sut = new TenantDatabaseUserHealer(_time);

        Assert.False(await sut.EvictOnAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance, CommandFailure(18)));
    }

    [Theory]
    [InlineData("Command aggregate requires authentication", true)]
    [InlineData("not authorized on tmpqa to execute command { find: \"x\" }", true)]
    [InlineData("Authentication failed.", true)]
    [InlineData("Duplicate key", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Authentication_failures_are_recognised_by_their_message(string? message, bool expected) =>
        Assert.Equal(expected, TenantDatabaseUserHealer.IsAuthenticationFailureMessage(message));

    [Fact]
    public async Task The_first_eviction_re_ensures_the_user_even_when_a_heal_ran_within_the_last_minute()
    {
        // Round 3: an earlier heal (tenant create, reconciler) used to consume the one-minute heal limit, so the
        // re-create came only with the third eviction.
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x"))
            .Returns(TenantDatabaseUserEnsureResult.AlreadyPresent);
        var sut = new TenantDatabaseUserHealer(_time);
        await sut.TryHealAsync(_systemContext, "x", NullLogger.Instance);
        _time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(await sut.EvictAfterAuthenticationFailureAsync(_systemContext, "x", NullLogger.Instance));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Fake.GetCalls(_systemContext)
                   .Count(c => c.Method.Name == nameof(ISystemContext.EnsureTenantDatabaseUserAsync)) < 2)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).MustHaveHappenedTwiceExactly();
    }
}
