using System.Net;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>AB#8228 — authentication failures escaping an octo service evict the addressed tenant's clients.</summary>
public class TenantPoolEvictionMiddlewareTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    private static MongoCommandException AuthFailure() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))), "requires authentication",
            new BsonDocument(), new BsonDocument { { "code", 13 } });

    private HttpContext Context(string path) => new DefaultHttpContext
    {
        Request = { Path = path },
        RequestServices = new ServiceCollection().AddSingleton(_systemContext).BuildServiceProvider()
    };

    [Theory]
    [InlineData("/meshtest/v1/entities", "meshtest")]
    [InlineData("/tenants/meshtest/GraphQL", "meshtest")]
    public async Task AuthenticationFailure_evicts_the_tenant_of_the_request_and_rethrows(string path, string tenant)
    {
        var sut = new TenantPoolEvictionMiddleware(_ => throw AuthFailure(), new TenantDatabaseUserHealer());

        await Assert.ThrowsAsync<MongoCommandException>(() => sut.InvokeAsync(Context(path)));

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(tenant, null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Repeated_failures_within_the_window_evict_only_once()
    {
        var sut = new TenantPoolEvictionMiddleware(_ => throw AuthFailure(), new TenantDatabaseUserHealer());

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<MongoCommandException>(() => sut.InvokeAsync(Context("/meshtest/x")));
        }

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Other_errors_and_successful_requests_do_not_evict()
    {
        var failing = new TenantPoolEvictionMiddleware(_ => throw new InvalidOperationException(), new TenantDatabaseUserHealer());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InvokeAsync(Context("/meshtest/x")));

        var ok = new TenantPoolEvictionMiddleware(_ => Task.CompletedTask, new TenantDatabaseUserHealer());
        await ok.InvokeAsync(Context("/meshtest/x"));

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
