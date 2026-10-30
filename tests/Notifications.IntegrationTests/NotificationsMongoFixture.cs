using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MongoDb;

namespace Notifications.IntegrationTests;

/// <summary>
///     A real MongoDB replica set (Testcontainers) with the engine's system tenant, one child tenant and the
///     <c>System.Notification</c> model imported into both, so the event repository and the audit sink write
///     into the real persistence (AB#7821). When Docker is not available (or <c>OCTO_SKIP_CONTAINER_TESTS=true</c>)
///     the tests are reported as skipped, not failed.
/// </summary>
public sealed class NotificationsMongoFixture : IAsyncLifetime
{
    public const string ChildTenantId = "eventlogchild";

    // An exact patch version, never the floating mongo:8.0 tag (SERVER-121912 on newer kernels), same as the engine's suite.
    private const string MongoImage = "mongo:8.0.15";
    private const string AdminUser = "octo-system-admin";

    private MongoDbContainer? _container;

    public string? SkipReason { get; private set; }

    public ServiceProvider? Provider { get; private set; }

    public ISystemContext SystemContext => Provider!.GetRequiredService<ISystemContext>();

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("OCTO_SKIP_CONTAINER_TESTS"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "OCTO_SKIP_CONTAINER_TESTS=true";
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _container = new MongoDbBuilder(MongoImage)
                .WithReplicaSet()
                .WithUsername(AdminUser)
                .WithPassword("OctoAdmin1")
                .Build();
            await _container.StartAsync(timeout.Token);
        }
        catch (Exception e)
        {
            SkipReason = $"MongoDB container could not be started (Docker unavailable?): {e.GetType().Name}: {e.Message}";
            return;
        }

        var host = $"localhost:{_container.GetMappedPublicPort()}";
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRuntimeEngine().AddMongoDbRuntimeRepository();
        services.AddOctoNotification();
        services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = $"eventlogsystem{Guid.NewGuid():N}";
            t.DatabaseHost = host;
            t.AdminUser = AdminUser;
            t.AdminUserPassword = "OctoAdmin1";
            t.DatabaseUserPassword = "OctoUser1";
            t.UseDirectConnection = true;
        });
        Provider = services.BuildServiceProvider();

        var systemContext = SystemContext;
        await systemContext.CreateSystemTenantAsync();
        await ((ITenantContext)systemContext).ImportCkModelAsync(NotificationModelId, new OperationResult());

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, ChildTenantId, ChildTenantId);
            await session.CommitTransactionAsync();
        }

        ITenantContext child;
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            child = await systemContext.GetChildTenantContextAsync(session, ChildTenantId);
            await session.CommitTransactionAsync();
        }

        await child.ImportCkModelAsync(NotificationModelId, new OperationResult());
    }

    public static CkModelId NotificationModelId { get; } = new("System.Notification-2.7.0");

    public void SkipIfUnavailable()
    {
        Assert.SkipWhen(SkipReason != null, SkipReason ?? string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
