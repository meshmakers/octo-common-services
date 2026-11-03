using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     Unit tests for <see cref="TenantMiddleware" />. Focus is the AB#4287 regression fix:
///     the feature-enabled gate (403 when the feature CanBeEnabled but is NOT enabled for the
///     tenant) must NOT block the feature lifecycle endpoints (…/enable, …/disable), because
///     those endpoints manage that very state and would otherwise become impossible to re-enable.
/// </summary>
public class TenantMiddlewareTests
{
    private const string TenantId = "meshtest";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IConfigurationService _configurationService = A.Fake<IConfigurationService>();

    public TenantMiddlewareTests()
    {
        var tenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => tenantRepository.TenantId).Returns(TenantId);
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync(TenantId)).Returns(tenantRepository);

        // Feature can be enabled but is currently DISABLED for the tenant -> the gate would fire.
        A.CallTo(() => _configurationService.CanBeEnabled()).Returns(true);
        A.CallTo(() => _configurationService.IsEnabledAsync(TenantId)).Returns(false);
    }

    private static HttpContext CreateContext(string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.RouteValues = new RouteValueDictionary
        {
            { InfrastructureCommon.TenantIdRoute, TenantId }
        };
        return httpContext;
    }

    private async Task<(HttpContext context, bool nextCalled)> InvokeAsync(string path)
    {
        var context = CreateContext(path);
        var nextCalled = false;
        var middleware = new TenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _systemContext, _configurationService);
        return (context, nextCalled);
    }

    [Theory]
    [InlineData("/meshtest/v1/reporting/enable")]
    [InlineData("/meshtest/v1/reporting/disable")]
    public async Task LifecycleEndpoint_WhenFeatureDisabled_IsAllowedThrough(string path)
    {
        // Act
        var (context, nextCalled) = await InvokeAsync(path);

        // Assert — the enabled-gate is skipped for lifecycle endpoints
        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        // Tenant resolution must still run (the enable/disable controller needs the tenant repo)
        Assert.NotNull(context.Items[InfrastructureCommon.TenantRepositoryName]);
        Assert.Equal(TenantId, context.Items[InfrastructureCommon.TenantIdName]);
    }

    [Fact]
    public async Task NonLifecycleEndpoint_WhenFeatureDisabled_Returns403()
    {
        // Act — a regular tenant-scoped route must still be gated
        var (context, nextCalled) = await InvokeAsync("/meshtest/v1/reports");

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static MongoCommandException AuthFailure() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))), "requires authentication",
            new BsonDocument(), new BsonDocument { { "code", 13 } });

    [Fact]
    public async Task AuthenticationFailure_downstream_evicts_the_tenants_clients_and_rethrows()
    {
        var healer = new TenantDatabaseUserHealer();
        var context = CreateContext("/meshtest/v1/something");
        var middleware = new TenantMiddleware(_ => throw AuthFailure(), healer);
        A.CallTo(() => _configurationService.CanBeEnabled()).Returns(false);

        await Assert.ThrowsAsync<MongoCommandException>(() => middleware.InvokeAsync(context, _systemContext, _configurationService));

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(TenantId, null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Other_downstream_errors_do_not_evict()
    {
        var healer = new TenantDatabaseUserHealer();
        var context = CreateContext("/meshtest/v1/something");
        var middleware = new TenantMiddleware(_ => throw new InvalidOperationException("boom"), healer);
        A.CallTo(() => _configurationService.CanBeEnabled()).Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, _systemContext, _configurationService));

        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(A<string>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
