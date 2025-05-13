using FakeItEasy;
using Meshmakers.Octo.Services.Infrastructure.Migrations;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.Tests.Tests;

public class MigrationServiceTests
{
    private readonly IDbConfigVersionManager _versionManager;
    private readonly IMigrationLoader _loader;
    private readonly ILogger<MigrationService> _logger;
    private readonly MigrationService _service;

    public MigrationServiceTests()
    {
        _versionManager = A.Fake<IDbConfigVersionManager>();
        _loader = A.Fake<IMigrationLoader>();
        _logger = A.Fake<ILogger<MigrationService>>();
        _service = new(_versionManager, [], _loader, _logger);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SingleConfig_Success()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(0, 1, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1"))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);
        A.CallTo(() => migration.MigrateAsync())
            .Returns(new MigrationResult());

        await _service.ExecuteMigrationsAsync();

        A.CallTo(() => _versionManager.UpdateVersionAsync("Config1", 1))
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

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1"))
            .Returns(0);
        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config2"))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext1, configContext2]);
        A.CallTo(() => migration1.MigrateAsync())
            .Returns(new MigrationResult());
        A.CallTo(() => migration2.MigrateAsync())
            .Returns(new MigrationResult());

        await _service.ExecuteMigrationsAsync();

        A.CallTo(() => _versionManager.UpdateVersionAsync("Config1", 1))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _versionManager.UpdateVersionAsync("Config2", 1))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_VersionMismatch_ThrowsException()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(1, 2, "Config1"); // Expects version 1
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1"))
            .Returns(0); // Current version is 0
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteMigrationsAsync());
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_MigrationFails_ThrowsException()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(0, 1, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1"))
            .Returns(0);
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);
        A.CallTo(() => migration.MigrateAsync())
            .Returns(MigrationResult.Failure("Errors"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteMigrationsAsync());
        Assert.Contains("Errors", exception.Message);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_NoPendingMigrations_SkipsExecution()
    {
        var migration = A.Fake<IMigration>();
        var attribute = new MigrationAttribute(1, 2, "Config1");
        var migrationContext = new MigrationContext(migration, attribute);
        var configContext = new ConfigMigrationContext("Config1", [migrationContext]);

        A.CallTo(() => _versionManager.GetCurrentVersionAsync("Config1"))
            .Returns(2); // Current version is already at target
        A.CallTo(() => _loader.GetMigrationContextsPerConfig(A<IEnumerable<IMigration>>._))
            .Returns([configContext]);

        await _service.ExecuteMigrationsAsync();

        A.CallTo(() => migration.MigrateAsync())
            .MustNotHaveHappened();
        A.CallTo(() => _versionManager.UpdateVersionAsync(A<string>._, A<int>._))
            .MustNotHaveHappened();
    }
}
