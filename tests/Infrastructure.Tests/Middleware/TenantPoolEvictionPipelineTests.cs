using System.Net;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     AB#8228 round 2 — the eviction must also work behind a real exception-swallowing layer. In the first round the
///     middleware was only tested with a hand-made delegate; behind <c>UseDeveloperExceptionPage()</c> (local stack) or
///     <c>UseExceptionHandler()</c> the exception never reached it, so the asset repository never evicted.
/// </summary>
public class TenantPoolEvictionPipelineTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    private static MongoCommandException AuthFailure() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))),
            "Command aggregate failed: Command aggregate requires authentication.",
            new BsonDocument(), new BsonDocument { { "code", 13 } });

    private async Task<IHost> StartAsync(string environment, Action<IApplicationBuilder> exceptionLayer,
        bool useController = true)
    {
        var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddLogging();
                    s.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly);
                    s.AddSingleton(_systemContext);
                    s.AddSingleton(new TenantDatabaseUserHealer()); // own rate limit, not the process-wide one
                    s.AddSingleton<ThrowingController.Thrower>(_ => AuthFailure);
                    s.AddExceptionHandler<OctoExceptionHandler>();
                    s.AddTenantPoolEviction();
                });
                web.Configure(app =>
                {
                    exceptionLayer(app);
                    if (useController)
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapControllers());
                    }
                    else
                    {
                        app.Run(_ => throw AuthFailure());
                    }
                });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private void AssertEvicted(string tenant) =>
        A.CallTo(() => _systemContext.InvalidateTenantRepositoryClientsAsync(tenant, null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

    [Fact]
    public async Task Behind_the_developer_exception_page_the_tenant_is_still_evicted()
    {
        using var host = await StartAsync(Environments.Development, app => app.UseDeveloperExceptionPage());
        var response = await host.GetTestClient().GetAsync("/tmpqa/v1/throw", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertEvicted("tmpqa");
    }

    [Fact]
    public async Task Behind_UseExceptionHandler_the_tenant_is_evicted_also_without_a_controller()
    {
        using var host = await StartAsync(Environments.Production, app => app.UseExceptionHandler(h =>
            h.Run(ctx =>
            {
                ctx.Response.StatusCode = 500;
                return Task.CompletedTask;
            })), useController: false);
        var response = await host.GetTestClient().GetAsync("/tmpqa/v1/throw", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertEvicted("tmpqa");
    }

    [Fact]
    public async Task Without_an_exception_layer_the_tenant_is_evicted()
    {
        using var host = await StartAsync(Environments.Production, _ => { }, useController: false);
        await Assert.ThrowsAnyAsync<Exception>(() => host.GetTestClient().GetAsync("/tmpqa/v1/throw", TestContext.Current.CancellationToken));

        AssertEvicted("tmpqa");
    }
}

/// <summary>Controller whose action fails with the MongoDB error the asset repository sees after a user drop.</summary>
[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("{tenantId}/v1/throw")]
public class ThrowingController(ThrowingController.Thrower thrower) : Microsoft.AspNetCore.Mvc.ControllerBase
{
    public delegate Exception Thrower();

    [Microsoft.AspNetCore.Mvc.HttpGet]
    public Microsoft.AspNetCore.Mvc.IActionResult Get() => throw thrower();
}
