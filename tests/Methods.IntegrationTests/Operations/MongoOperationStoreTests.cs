using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.MongoDb;
using Meshmakers.Octo.Services.Methods.Operations;
using Meshmakers.Octo.Services.Methods.Tests.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests.Operations;

/// <summary>
///     A private MongoDB replica set (Testcontainers) for the operation store tests (AB#8443). Without Docker, or with
///     <c>OCTO_SKIP_CONTAINER_TESTS=true</c>, the tests are reported as skipped, not failed.
/// </summary>
public sealed class OperationsMongoFixture : IAsyncLifetime
{
    // an exact patch version, never the floating tag (same image as the Notifications integration tests)
    private const string MongoImage = "mongo:8.0.15";

    private MongoDbContainer? _container;

    public string? SkipReason { get; private set; }

    public MongoClient Client { get; private set; } = null!;

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
            _container = new MongoDbBuilder(MongoImage).WithReplicaSet().Build();
            await _container.StartAsync(timeout.Token);
        }
        catch (Exception e)
        {
            SkipReason = $"MongoDB container could not be started (Docker unavailable?): {e.GetType().Name}";
            return;
        }

        ConnectionString = _container.GetConnectionString();
        Client = new MongoClient(ConnectionString);
    }

    /// <summary>Connection string of the container (host, port and the container's credentials).</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A fresh database (one per store), standing in for the system database.</summary>
    public IMongoDatabase NewDatabase()
    {
        Assert.SkipWhen(SkipReason != null, SkipReason ?? string.Empty);
        return Client.GetDatabase($"opsystem{Guid.NewGuid():N}");
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>AB#8443: the store contract against <see cref="CkMethodMongoOperationStore" /> on a real replica set.</summary>
public class MongoOperationStoreTests(OperationsMongoFixture mongo)
    : OperationStoreContract, IClassFixture<OperationsMongoFixture>
{
    protected override Task<ICkMethodOperationStore> CreateStoreAsync() =>
        Task.FromResult<ICkMethodOperationStore>(new CkMethodMongoOperationStore(mongo.NewDatabase(), Time,
            NullLogger<CkMethodMongoOperationStore>.Instance));
}

/// <summary>
///     AB#8881 (security): on a real replica set, a tenant created again with the id of a deleted tenant reads none of the
///     deleted tenant's records (failed purge, late write after the purge), and the setup purge removes them.
/// </summary>
public class MongoTenantGenerationStoreTests(OperationsMongoFixture mongo)
    : TenantGenerationStoreContract, IClassFixture<OperationsMongoFixture>
{
    protected override ICkMethodOperationStore CreateInner() =>
        new CkMethodMongoOperationStore(mongo.NewDatabase(), Time, NullLogger<CkMethodMongoOperationStore>.Instance);
}

/// <summary>
///     AB#8443: what only the real database shows: indexes, durability across store instances (a service restart), a
///     handler host that dies while it works, and the raw documents (no parameters, no token).
/// </summary>
public class MongoOperationRuntimeTests(OperationsMongoFixture mongo) : IClassFixture<OperationsMongoFixture>
{
    private const string MethodId = "Test.Ops/Backup.Dump-1";
    private const string Secret = "S3cr3t-Pa55word!";
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.payload.signature";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Indexes_UniqueTenantOperation_AndTtlOnExpiresAt()
    {
        var database = mongo.NewDatabase();
        var store = new CkMethodMongoOperationStore(database, _time, NullLogger<CkMethodMongoOperationStore>.Instance);
        await store.ListAsync(new CkMethodOperationQuery { TenantId = "t1" }, TestContext.Current.CancellationToken);

        var indexes = await (await database.GetCollection<BsonDocument>(CkMethodMongoOperationStore.CollectionName)
            .Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);

        var unique = Assert.Single(indexes, i => i["name"] == CkMethodMongoOperationStore.UniqueIndexName);
        Assert.True(unique["unique"].ToBoolean());
        Assert.Equal(new BsonDocument { { "tenantId", 1 }, { "operationId", 1 } }, unique["key"].AsBsonDocument);
        var ttl = Assert.Single(indexes, i => i["name"] == CkMethodMongoOperationStore.TtlIndexName);
        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task FinishedOperation_IsReadableAfterARestart_ByAnotherStoreInstance()
    {
        var database = mongo.NewDatabase();
        var first = new CkMethodMongoOperationStore(database, _time, NullLogger<CkMethodMongoOperationStore>.Instance);
        var now = _time.GetUtcNow().UtcDateTime;
        var record = new CkMethodOperationRecord
        {
            TenantId = "t1", OperationId = "op-restart", MethodId = MethodId, InvocationId = Guid.NewGuid(),
            HandlerInstanceId = "h-1", AcceptedAtUtc = now, HeartbeatAtUtc = now
        };
        await first.CreateAsync(record, TestContext.Current.CancellationToken);
        await first.CompleteAsync("t1", "op-restart", new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded, ResultJson = "42", CompletedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7)
        }, TestContext.Current.CancellationToken);
        first.Dispose();

        // a new process: its own client and store instance on the same database
        using var client = new MongoClient(mongo.Client.Settings.Clone());
        var second = new CkMethodMongoOperationStore(client.GetDatabase(database.DatabaseNamespace.DatabaseName), _time,
            NullLogger<CkMethodMongoOperationStore>.Instance);
        var read = await second.GetAsync("t1", "op-restart", TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodOperationState.Succeeded, read!.State);
        Assert.Equal("42", read.ResultJson);
    }

    [Fact]
    public async Task HandlerHostKilledWhileWorking_SweepOfAnotherHostEndsItAsHandlerLost_WorkNeverRunsAgain()
    {
        var database = mongo.NewDatabase();
        var started = new TaskCompletionSource();
        var runs = 0;
        var handler = new DumpHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return CkMethodOutcome.Ok();
        });

        // host A accepts and starts the work, then is "killed": no heartbeat, no graceful stop, never completes
        var hostA = Host.Build(database, _time, handler);
        var response = await hostA.Invoker.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(response.IsAccepted, response.ToString());
        var operationId = response.Operation!.OperationId;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var running = await hostA.Store.GetAsync("meshtest", operationId, TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodOperationState.Running, running!.State);

        // host B (another replica, after a restart) only sweeps
        var hostB = Host.Build(database, _time, new DumpHandler((_, _) => Task.FromResult(CkMethodOutcome.Ok())));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await hostB.Runner.SweepAsync(TestContext.Current.CancellationToken));
        _time.Advance(new CkMethodOperationOptions().StaleAfter);
        Assert.Equal(1, await hostB.Runner.SweepAsync(TestContext.Current.CancellationToken));

        var lost = await hostB.Store.GetAsync("meshtest", operationId, TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodOperationState.Failed, lost!.State);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, lost.Error!.Code);
        Assert.Equal(CkMethodOperationReasons.HandlerLost, lost.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(_time.GetUtcNow().UtcDateTime + CkMethodOperationOptions.DefaultRetention, lost.ExpiresAtUtc);
        Assert.Equal(1, runs);
        Assert.Equal(0, hostB.Runner.RunningCount);

        // the raw document carries no parameter value and no token
        var raw = await database.GetCollection<BsonDocument>(CkMethodMongoOperationStore.CollectionName)
            .Find(new BsonDocument("operationId", operationId)).SingleAsync(TestContext.Current.CancellationToken);
        var text = raw.ToJson();
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(Token, text);

        await hostA.Runner.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OperationIds_AreUniquePerTenant_TheSameIdInTwoTenantsIsAllowed()
    {
        var store = new CkMethodMongoOperationStore(mongo.NewDatabase(), _time,
            NullLogger<CkMethodMongoOperationStore>.Instance);
        var now = _time.GetUtcNow().UtcDateTime;
        CkMethodOperationRecord Record(string tenant) => new()
        {
            TenantId = tenant, OperationId = "same", MethodId = MethodId, InvocationId = Guid.NewGuid(),
            HandlerInstanceId = "h", AcceptedAtUtc = now, HeartbeatAtUtc = now
        };

        await store.CreateAsync(Record("t1"), TestContext.Current.CancellationToken);
        await store.CreateAsync(Record("t2"), TestContext.Current.CancellationToken);

        Assert.Equal("t2", (await store.GetAsync("t2", "same", TestContext.Current.CancellationToken))!.TenantId);
    }

    private CkMethodRequest Request() => new("meshtest")
    {
        InvocationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Test.Ops/Backup",
        ParametersJson = $"{{\"password\":\"{Secret}\"}}",
        CallerAccessToken = Token,
        DeadlineUtc = _time.GetUtcNow().UtcDateTime.AddSeconds(30)
    };

    public sealed record DumpParameters
    {
        public string? Password { get; init; }
    }

    private sealed class DumpHandler(
        Func<ICkMethodOperationProgress, CancellationToken, Task<CkMethodOutcome>> work)
        : ICkMethodHandler<DumpParameters>
    {
        public Task<CkMethodOutcome> HandleAsync(CkMethodContext<DumpParameters> context,
            CancellationToken cancellationToken) => Task.FromResult(CkMethodOutcome.Accepted(work));
    }

    private sealed class Definitions(CkMethodDto definition) : ICkMethodDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) => definition;
    }

    private sealed class Principal : ICkMethodCallerTokenValidator
    {
        public ValueTask<CkMethodTokenValidationResult> ValidateAsync(string? accessToken, string expectedTenantId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CkMethodTokenValidationResult.Valid(new CkMethodPrincipal("user-1",
                [CkMethodAuthorizationOptions.DefaultAdministratorRoles.First()], [CkMethodAuthorizationOptions.ApiScope])));
    }

    /// <summary>One handler host: the Mongo store on the shared database, the operation runtime and an invoker.</summary>
    private sealed record Host(ICkMethodHandlerInvoker Invoker, ICkMethodOperationStore Store,
        CkMethodOperationRunner Runner)
    {
        public static Host Build(IMongoDatabase database, TimeProvider time, DumpHandler handler)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(time);
            services.AddLogging();
            services.AddScoped(_ => handler);
            services.AddSingleton<ICkMethodOperationStore>(new CkMethodMongoOperationStore(database, time,
                NullLogger<CkMethodMongoOperationStore>.Instance));
            services.AddCkMethodOperations();
            var provider = services.BuildServiceProvider();

            var definition = new CkMethodDto
            {
                MethodId = "Dump-1",
                Parameters = [new CkMethodParameterDto { Name = "password", ValueType = AttributeValueTypesDto.String, Sensitive = true, IsOptional = true }],
                Execution = new CkMethodExecutionDto { Mode = CkMethodModes.Async }
            };
            var registrations = new CkMethodHandlerRegistrations();
            registrations.SetServiceName("BotServices");
            registrations.Add(MethodId, typeof(DumpHandler));
            new CkMethodRegistrationValidator(new Definitions(definition)).Validate(registrations);
            var instance = new CkMethodHandlerInstance(registrations, Options.Create(new CkMethodHandlerOptions()), time);
            var invoker = new CkMethodHandlerInvoker(provider.GetRequiredService<IServiceScopeFactory>(), registrations,
                instance, time, provider.GetRequiredService<ILogger<CkMethodHandlerInvoker>>(),
                new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
                tokenValidator: new Principal());
            return new Host(invoker, provider.GetRequiredService<ICkMethodOperationStore>(),
                provider.GetRequiredService<CkMethodOperationRunner>());
        }
    }
}

/// <summary>
///     AB#8444: the store built from the system configuration (the production constructor) uses the database name the
///     engine uses: trimmed and lower-case. MongoDB refuses a database name that differs from an existing one only in case,
///     so the configured spelling (default <c>OctoSystem</c>) failed every write on a local stack.
/// </summary>
public class MongoOperationStoreSystemDatabaseTests(OperationsMongoFixture mongo) : IClassFixture<OperationsMongoFixture>
{
    [Theory]
    [InlineData("OctoSystem", "octosystem")]
    [InlineData("octosystem", "octosystem")]
    public void SystemDatabaseName_IsTrimmedAndLowerCase(string configured, string expected)
    {
        Assert.Equal(expected, CkMethodMongoOperationStore.SystemDatabaseName(
            new Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration.OctoSystemConfiguration { SystemDatabaseName = configured }));
    }

    [Fact]
    public async Task StoreFromConfiguration_WritesIntoTheLowerCaseSystemDatabase_ThatAlreadyExists()
    {
        mongo.NewDatabase(); // skips without Docker
        var lower = $"opsysmixed{Guid.NewGuid():N}";
        // the engine created the system database in lower case before (here: one document)
        await mongo.Client.GetDatabase(lower).GetCollection<BsonDocument>("tenant_lifecycle")
            .InsertOneAsync(new BsonDocument("tenantId", "octosystem"), cancellationToken: TestContext.Current.CancellationToken);

        var url = new MongoUrl(mongo.ConnectionString);
        var configuration = new Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration.OctoSystemConfiguration
        {
            SystemDatabaseName = "OpSysMixed" + lower["opsysmixed".Length..],
            DatabaseHost = $"{url.Server.Host}:{url.Server.Port}",
            AdminUser = url.Username,
            AdminUserPassword = url.Password,
            UseDirectConnection = true
        };
        using var store = new CkMethodMongoOperationStore(Options.Create(configuration), TimeProvider.System,
            NullLogger<CkMethodMongoOperationStore>.Instance);
        var now = DateTime.UtcNow;

        await store.CreateAsync(new CkMethodOperationRecord
        {
            TenantId = "t1", OperationId = "op-mixed", MethodId = "Test.Ops/Backup.Dump-1", InvocationId = Guid.NewGuid(),
            HandlerInstanceId = "h1", AcceptedAtUtc = now, HeartbeatAtUtc = now
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetAsync("t1", "op-mixed", TestContext.Current.CancellationToken));
        Assert.Equal(1, await mongo.Client.GetDatabase(lower).GetCollection<BsonDocument>(
            CkMethodMongoOperationStore.CollectionName).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
