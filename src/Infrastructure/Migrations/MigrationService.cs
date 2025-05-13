namespace Meshmakers.Octo.Services.Infrastructure.Migrations
{
    public class MigrationService
    {
        private readonly IDbConfigVersionManager _versionManager;
        private readonly IEnumerable<IMigration> _migrationTypes;
        private readonly IMigrationLoader _loader;
        private readonly ILogger<MigrationService> _logger;

        public MigrationService(
            IDbConfigVersionManager versionManager,
            IEnumerable<IMigration> migrationTypes,
            IMigrationLoader loader,
            ILogger<MigrationService> logger)
        {
            _versionManager = versionManager;
            _migrationTypes = migrationTypes;
            _loader = loader;
            _logger = logger;
        }

        public async Task ExecuteMigrationsAsync()
        {
            _logger.LogInformation("Starting execution of migrations.");

            var migrationsPerConfig = _loader.GetMigrationContextsPerConfig(_migrationTypes);

            foreach (var (configName, migrationContexts) in migrationsPerConfig)
            {
                _logger.LogDebug("Processing config '{ConfigName}' with {MigrationCount} migration(s).", configName, migrationContexts.Length);

                var currentVersion = await _versionManager.GetCurrentVersionAsync(configName).ConfigureAwait(false);
                _logger.LogDebug("Current version for config '{ConfigName}' is {CurrentVersion}.", configName, currentVersion);

                var pending = migrationContexts
                    .Where(x => x.Attribute.FromVersion >= currentVersion)
                    .ToList();

                _logger.LogInformation("{PendingCount} pending migration(s) found for config '{ConfigName}'.", pending.Count, configName);

                foreach (var m in pending)
                {
                    var migrationName = m.Migration.GetType().Name;
                    var desc = m.Attribute.Description ?? string.Empty;

                    _logger.LogDebug("Preparing to apply migration '{MigrationName}' ({Description}) for config '{ConfigName}' from version {FromVersion} to {ToVersion}.",
                        migrationName, desc, configName, m.Attribute.FromVersion, m.Attribute.ToVersion);

                    if (m.Attribute.FromVersion != currentVersion)
                    {
                        _logger.LogError("Version mismatch for migration '{MigrationName}' ({Description}). Expected from version {ExpectedVersion}, but found {ActualVersion}.",
                            migrationName, desc, currentVersion, m.Attribute.FromVersion);

                        throw new InvalidOperationException(
                            $"Migration '{migrationName}' ({desc}) has invalid FromVersion {m.Attribute.FromVersion} for config {configName}. Expected {currentVersion}.");
                    }

                    var result = await m.Migration.MigrateAsync().ConfigureAwait(false);

                    if (result.HasError)
                    {
                        _logger.LogError("Migration '{MigrationName}' ({Description}) failed with error: {ErrorText}.",
                            migrationName, desc, result.ErrorText);

                        throw new InvalidOperationException(
                            $"Migration {migrationName} ({desc}) failed: {result.ErrorText}");
                    }

                    currentVersion = m.Attribute.ToVersion;

                    _logger.LogInformation("Migration '{MigrationName}' ({Description}) applied successfully. Updating version to {NewVersion}.",
                        migrationName, desc, currentVersion);

                    await _versionManager.UpdateVersionAsync(configName, currentVersion).ConfigureAwait(false);
                }

                _logger.LogInformation("All migrations for config '{ConfigName}' applied successfully. Final version is {FinalVersion}.", configName, currentVersion);
            }

            _logger.LogInformation("All migrations executed successfully.");
        }
    }
}
