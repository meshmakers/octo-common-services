using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Migrations;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Standardized default configuration creator service
/// </summary>
/// <remarks>
/// This class is used to create the default configuration for a tenant for a service. It is used to create
/// Identity data for Identity Service using Distribution Event Hub, import CK model and create default data. There are methods
/// <see cref="StartTenantAsync"/> and <see cref="StopTenantAsync"/> which can be overridden to start and stop additional services
/// specific to the service.
///
/// There are two modes: A service is enabled for a tenant manually or automatically. If a service is enabled manually
/// during startup, only identity data is created. If a service is enabled automatically, the schema version and
/// default data get created.
/// </remarks>
public abstract class DefaultConfigurationCreatorServiceStandardized : DefaultConfigurationCreatorServiceBase,
    IConfigurationService
{
    private readonly ILogger<DefaultConfigurationCreatorServiceStandardized> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _createIdentityDataCommandClient;
    private readonly MigrationService? _migrationService;
    private readonly ICkModelUpgradeService? _ckModelUpgradeService;
    private readonly IRuntimeRepositoryProvider? _runtimeRepositoryProvider;
    private readonly string? _serviceEnabledKey;
    private readonly bool? _autoEnable;
    private readonly string _identityDataVersionKey;
    private readonly int _expectedIdentityDataVersion;
    private readonly List<string> _deferredStartTenantIds = new();

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Logger</param>
    /// <param name="systemContext">System context of repository</param>
    /// <param name="createIdentityDataCommandClient">The command client to create identity data</param>
    /// <param name="identityDataVersionKey">The configuration key for the identity data version</param>
    /// <param name="expectedIdentityDataVersion">The expected value of the identity data version</param>
    /// <param name="migrationService">The migration service</param>
    /// <param name="ckModelUpgradeService">The CK model upgrade service for data migrations</param>
    /// <param name="runtimeRepositoryProvider">The runtime repository provider for getting schema versions</param>
    /// <param name="serviceEnabledKey">The configuration key if the service is enabled for a tenant</param>
    /// <param name="autoEnable">When true, all tenants are automatically enabled</param>
    protected DefaultConfigurationCreatorServiceStandardized(
        ILogger<DefaultConfigurationCreatorServiceStandardized> logger,
        ISystemContext systemContext,
        ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
        string identityDataVersionKey,
        int expectedIdentityDataVersion,
        MigrationService? migrationService = null,
        ICkModelUpgradeService? ckModelUpgradeService = null,
        IRuntimeRepositoryProvider? runtimeRepositoryProvider = null,
        string? serviceEnabledKey = null,
        bool? autoEnable = false)
        : base(logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _createIdentityDataCommandClient = createIdentityDataCommandClient;
        _migrationService = migrationService;
        _ckModelUpgradeService = ckModelUpgradeService;
        _runtimeRepositoryProvider = runtimeRepositoryProvider;
        _serviceEnabledKey = serviceEnabledKey;
        _autoEnable = autoEnable;
        _identityDataVersionKey = identityDataVersionKey;
        _expectedIdentityDataVersion = expectedIdentityDataVersion;
    }

    /// <inheritdoc />
    public async Task EnableAsync(string tenantId)
    {
        if (_serviceEnabledKey == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        if (_autoEnable.GetValueOrDefault())
        {
            throw ConfigurationException.TenantIsAutoEnabled(tenantId);
        }

        if (await IsEnabledAsync(tenantId).ConfigureAwait(false))
        {
            throw ConfigurationException.TenantAlreadyEnabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        // Capture schema versions BEFORE importing new CK models
        IReadOnlyDictionary<string, string>? previousSchemaVersions = null;
        if (_runtimeRepositoryProvider != null && _ckModelUpgradeService != null)
        {
            previousSchemaVersions = await _runtimeRepositoryProvider
                .GetSchemaVersionsAsync(tenantId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        await tenantContext.SetConfigurationAsync(session, _serviceEnabledKey,
            new DefaultConfigurationEnabled { IsEnabled = true }).ConfigureAwait(false);

        await ImportCkModelAsync(session, tenantContext).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        // start the tenant
        await StartTenantAsyncInternal(tenantContext, previousSchemaVersions).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisableAsync(string tenantId)
    {
        if (_serviceEnabledKey == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        if (_autoEnable.GetValueOrDefault())
        {
            throw ConfigurationException.TenantIsAutoEnabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationEnabled =
            await tenantContext.GetConfigurationAsync(session, _serviceEnabledKey,
                new DefaultConfigurationEnabled { IsEnabled = false }).ConfigureAwait(false);
        if (configurationEnabled == null || !configurationEnabled.IsEnabled)
        {
            throw ConfigurationException.TenantAlreadyDisabled(tenantId);
        }

        await tenantContext.DeleteConfigurationAsync(session, _serviceEnabledKey).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        await StopTenantAsync(tenantId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(string tenantId)
    {
        if (_serviceEnabledKey == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        if (_autoEnable.GetValueOrDefault())
        {
            throw ConfigurationException.TenantIsAutoEnabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, _serviceEnabledKey,
                new DefaultConfigurationEnabled { IsEnabled = false }).ConfigureAwait(false);
        if (configurationVersion == null)
        {
            return false;
        }

        return configurationVersion.IsEnabled;
    }

    /// <inheritdoc />
    public bool CanBeEnabled()
    {
        return _serviceEnabledKey != null
               && !_autoEnable.GetValueOrDefault();
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            _logger.LogInformation("System tenant does not exist. Skipping setup for tenant '{TenantId}'", tenantId);
            return;
        }

        _logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        // Capture schema versions BEFORE importing new CK models
        // This allows migrations to detect the previous version even without MigrationHistory
        IReadOnlyDictionary<string, string>? previousSchemaVersions = null;
        if (_runtimeRepositoryProvider != null && _ckModelUpgradeService != null)
        {
            previousSchemaVersions = await _runtimeRepositoryProvider
                .GetSchemaVersionsAsync(tenantId, CancellationToken.None)
                .ConfigureAwait(false);

            if (previousSchemaVersions.Count > 0)
            {
                _logger.LogDebug(
                    "Captured {Count} schema versions before import for tenant '{TenantId}'",
                    previousSchemaVersions.Count, tenantId);
            }
        }

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // Check if we need to create the identity data configuration for the services
        await CheckSetupIdentityDataAsync(session, tenantContext).ConfigureAwait(false);

        // Check if we need to import the CK model, we have two situations
        // 1. A service that can be enabled/disabled manually - in this case we check if the service is enabled for the tenant
        // 2. A service that is always enabled - in this case we always import the CK model
        if (CanBeEnabled())
        {
            if (await IsEnabledAsync(tenantId).ConfigureAwait(false))
            {
                await ImportCkModelAsync(session, tenantContext).ConfigureAwait(false);
            }
        }
        else
        {
            await ImportCkModelAsync(session, tenantContext).ConfigureAwait(false);
        }

        await session.CommitTransactionAsync().ConfigureAwait(false);

        if (CanBeEnabled() && await IsEnabledAsync(tenantId).ConfigureAwait(false)
            || !CanBeEnabled())
        {
            await StartTenantAsyncInternal(tenantContext, previousSchemaVersions).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    protected virtual Task StartTenantAsync(string tenantId)
    {
        _logger.LogInformation("Loading tenant '{TenantId}'", tenantId);

        // Currently left intentionally empty
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    protected virtual Task StopTenantAsync(string tenantId)
    {
        _logger.LogInformation("Unloading tenant '{TenantId}'", tenantId);

        // Currently left intentionally empty
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the identity API scope
    /// </summary>
    /// <param name="createIdentityDataCommandRequest">Request to create identity data</param>
    protected virtual void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Left intentionally empty for the derived classes to implement
    }

    /// <summary>
    /// Creates the identity API resources
    /// </summary>
    /// <param name="createIdentityDataCommandRequest">Request to create identity data</param>
    protected virtual void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Left intentionally empty for the derived classes to implement
    }

    /// <summary>
    /// Creates the identity API clients
    /// </summary>
    /// <param name="createIdentityDataCommandRequest">Request to create identity data</param>
    protected virtual void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Left intentionally empty for the derived classes to implement
    }

    /// <summary>
    /// Imports the CK model of the service if necessary
    /// </summary>
    /// <param name="session">Admin session</param>
    /// <param name="tenantContext">Tenant context</param>
    /// <returns></returns>
    protected virtual Task ImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // Left intentionally empty for the derived classes to implement
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the CK model IDs that this service uses.
    /// Override this method to provide the CK model IDs for CK model data migrations.
    /// </summary>
    /// <returns>Collection of CK model IDs with version ranges</returns>
    protected virtual IEnumerable<CkModelIdVersionRange> GetCkModelIds()
    {
        return Enumerable.Empty<CkModelIdVersionRange>();
    }

    private async Task CheckSetupIdentityDataAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // Identity configuration is next
        if (tenantContext.TenantId != _systemContext.TenantId)
        {
            // Currently we only support the system tenant.
            return;
        }

        _logger.LogInformation("Setting up default identity data for tenant '{TenantId}'", tenantContext.TenantId);

        var serviceConfiguration =
            await _systemContext.GetConfigurationAsync(session, _identityDataVersionKey,
                new DefaultConfigurationVersion { Version = -1 }).ConfigureAwait(false);
        if (serviceConfiguration == null ||
            serviceConfiguration.Version < _expectedIdentityDataVersion)
        {
            _logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantContext.TenantId);


            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(_systemContext.TenantId);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            _logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantContext.TenantId);
            var r = await _createIdentityDataCommandClient
                .GetResponseWithRetry<EnumCommandResponse<CreateIdentityDataResult>>(
                    createIdentityDataCommandRequest).ConfigureAwait(false);
            _logger.LogInformation("Create identity data response: {Response}", r.Response);
            if (r.Response == CreateIdentityDataResult.Success)
            {
                await _systemContext.SetConfigurationAsync(session,
                    _identityDataVersionKey,
                    new DefaultConfigurationVersion { Version = _expectedIdentityDataVersion }).ConfigureAwait(false);
            }
            else if (r.Response != CreateIdentityDataResult.FailedTenantHasNoIdentityCk)
            {
                _logger.LogInformation("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantContext.TenantId);
            }
            else
            {
                _logger.LogError("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantContext.TenantId);
            }
        }
    }

    private async Task RunMigrations(ITenantContext tenantContext)
    {
        // it is totally fine when the migration service is null. That means this service has no migrations
        if (_migrationService == null)
        {
            return;
        }

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        try
        {
            await _migrationService.ExecuteMigrationsAsync(session, tenantContext).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while running migrations. Aborting transaction");
            await session.AbortTransactionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunCkModelMigrationsAsync(
        ITenantContext tenantContext,
        IReadOnlyDictionary<string, string>? previousSchemaVersions = null)
    {
        if (_ckModelUpgradeService == null)
        {
            return;
        }

        var ckModelIds = GetCkModelIds().ToList();
        if (ckModelIds.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Running CK model data migrations for tenant '{TenantId}' with {ModelCount} models",
            tenantContext.TenantId, ckModelIds.Count);

        var result = await _ckModelUpgradeService.UpgradeModelsAsync(
            tenantContext.TenantId,
            ckModelIds,
            new CkMigrationOptions { ContinueOnError = false },
            previousSchemaVersions,
            CancellationToken.None).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMessage = $"CK model migration failed for tenant '{tenantContext.TenantId}': {string.Join("; ", result.Errors)}";
            _logger.LogError("{ErrorMessage}", errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        if (result.TotalEntitiesAffected > 0)
        {
            _logger.LogInformation(
                "CK model data migrations completed for tenant '{TenantId}': {EntitiesAffected} entities affected",
                tenantContext.TenantId, result.TotalEntitiesAffected);
        }
    }

    /// <inheritdoc />
    public override async Task StartDeferredTenantsAsync()
    {
        _logger.LogInformation("Starting {Count} deferred tenant(s)", _deferredStartTenantIds.Count);

        foreach (var tenantId in _deferredStartTenantIds)
        {
            _logger.LogInformation("Starting deferred tenant '{TenantId}'", tenantId);
            try
            {
                await StartTenantAsync(tenantId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start deferred tenant '{TenantId}'", tenantId);
                throw;
            }
        }

        _deferredStartTenantIds.Clear();
        DeferTenantStart = false;
    }

    private async Task StartTenantAsyncInternal(
        ITenantContext context,
        IReadOnlyDictionary<string, string>? previousSchemaVersions = null)
    {
        // 1. CK model data migrations (transforms existing runtime entities)
        await RunCkModelMigrationsAsync(context, previousSchemaVersions).ConfigureAwait(false);

        // 2. Infrastructure migrations (creates indexes, etc.)
        await RunMigrations(context).ConfigureAwait(false);

        // 3. Start tenant - defer if the bus is not yet available
        if (DeferTenantStart)
        {
            _logger.LogInformation(
                "Deferring start for tenant '{TenantId}' until distribution event hub is available",
                context.TenantId);
            _deferredStartTenantIds.Add(context.TenantId);
        }
        else
        {
            await StartTenantAsync(context.TenantId).ConfigureAwait(false);
        }
    }
}