using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Migrations;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.Tests.Tests;

public class MigrationServiceTests
{
    private readonly IDbConfigVersionManager _versionManager;
    private readonly IMigrationLoader _loader;
    private readonly ILogger<MigrationService> _logger;
    private readonly IOctoAdminSession _adminSession;
    private readonly ITenantContext _tenantContext;
    private readonly MigrationService _service;

    public MigrationServiceTests()
    {
        _versionManager = A.Fake<IDbConfigVersionManager>();
        _loader = A.Fake<IMigrationLoader>();
        _logger = A.Fake<ILogger<MigrationService>>();
        _adminSession = A.Fake<IOctoAdminSession>();
        _tenantContext = A.Fake<ITenantContext>();
        _service = new(_versionManager, [], _loader, _logger);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SingleConfig_Success()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(0, 1, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1", _adminSession, _tenantContext))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);
        A.CallTo(() => migration.MigrateAsync(_adminSession, _tenantContext))
            .Returns(new MigrationResult());

        await _service.ExecuteMigrationsAsync(_adminSession, _tenantContext);

        A.CallTo(() => _versionManager.UpdateVersionAsync("Config1", 1, _adminSession, _tenantContext))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_MultipleConfigs_Success()
    {
        var migration1 = A.Fake<IMigration>();
        var migration2 = A.Fake<IMigration>();
        var attribute1 = new MigrationAttribute(0, 1, "Config1");
        var attribute2 = new MigrationAttribute(0, 1, "Config2");
        var configContext1 = new ConfigMigrationContext("Config1", [new(migration1, attribute1)]);
        var configContext2 = new ConfigMigrationContext("Config2", [new(migration2, attribute2)]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1", _adminSession, _tenantContext))
            .Returns(0);
        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config2", _adminSession, _tenantContext))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext1, configContext2]);
        A.CallTo(() => migration1.MigrateAsync(_adminSession, _tenantContext))
            .Returns(new MigrationResult());
        A.CallTo(() => migration2.MigrateAsync(_adminSession, _tenantContext))
            .Returns(new MigrationResult());

        await _service.ExecuteMigrationsAsync(_adminSession, _tenantContext);

        A.CallTo(() => _versionManager.UpdateVersionAsync("Config1", 1, _adminSession, _tenantContext))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _versionManager.UpdateVersionAsync("Config2", 1, _adminSession, _tenantContext))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_VersionMismatch_ThrowsException()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(1, 2, "Config1"); // Expects version 1
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1", _adminSession, _tenantContext))
            .Returns(0); // Current version is 0
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteMigrationsAsync(_adminSession, _tenantContext));
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_MigrationFails_ThrowsException()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(0, 1, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1", _adminSession, _tenantContext))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);
        A.CallTo(() => migration.MigrateAsync(_adminSession, _tenantContext))
            .Returns(MigrationResult.Failure("Errors"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteMigrationsAsync(_adminSession, _tenantContext));
        Assert.Contains("Errors", exception.Message);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_NoPendingMigrations_SkipsExecution()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(1, 2, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1", _adminSession, _tenantContext))
            .Returns(2); // Current version is already at target
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);

        await _service.ExecuteMigrationsAsync(_adminSession, _tenantContext);

        A.CallTo(() => migration.MigrateAsync(_adminSession, _tenantContext))
            .MustNotHaveHappened();
        A.CallTo(() => _versionManager.UpdateVersionAsync(A<string>._, A<int>._, A<IOctoAdminSession>._, A<ITenantContext>._))
            .MustNotHaveHappened();
    }
    
    [Fact]
    public async Task ExecuteMigrationsAsync_NoMigrations_DoesNotThrow()
    {
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([]);

        await _service.ExecuteMigrationsAsync(_adminSession, _tenantContext);

        A.CallTo(() => _versionManager.UpdateVersionAsync(A<string>._, A<int>._, A<IOctoAdminSession>._, A<ITenantContext>._))
            .MustNotHaveHappened();
    }
}
