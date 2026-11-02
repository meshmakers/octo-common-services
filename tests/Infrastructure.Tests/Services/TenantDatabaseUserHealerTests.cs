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
}
