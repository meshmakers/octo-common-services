using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Standardized default configuration creator service
/// </summary>
/// <remarks>
/// This class is used to create the default configuration for a tenant for a service. It is used to create
/// Identity data for Identity Service using Distribution Event Hub, import CK model and create default data. There are methods
/// <see cref="StartTenantAsync"/> and <see cref="StopTenantAsync"/> which can be overridden to start and stop additional services
/// specific to the service.
/// </remarks>
public abstract class DefaultConfigurationCreatorServiceStandardized : DefaultConfigurationCreatorServiceBase,
    IConfigurationService
{
    private readonly ILogger<DefaultConfigurationCreatorServiceStandardized> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _createIdentityDataCommandClient;
    private readonly string? _schemaVersionKey;
    private readonly int? _expectedSchemaVersion;
    private readonly string _identityDataVersionKey;
    private readonly int _expectedIdentityDataVersion;
    private readonly string? _defaultDataVersionKey;
    private readonly int? _expectedDefaultDataVersion;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Logger</param>
    /// <param name="systemContext">System context of repository</param>
    /// <param name="createIdentityDataCommandClient">The command client to create identity data</param>
    /// <param name="schemaVersionKey">The configuration key for the schema version</param>
    /// <param name="expectedSchemaVersion">The expected value of the schema version</param>
    /// <param name="identityDataVersionKey">The configuration key for the identity data version</param>
    /// <param name="expectedIdentityDataVersion">The expected value of the identity data version</param>
    /// <param name="defaultDataVersionKey">The configuration key for the default data version</param>
    /// <param name="expectedDefaultDataVersion">The expected value of the default data version</param>
    protected DefaultConfigurationCreatorServiceStandardized(
        ILogger<DefaultConfigurationCreatorServiceStandardized> logger,
        ISystemContext systemContext,
        ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
        string identityDataVersionKey,
        int expectedIdentityDataVersion, string? schemaVersionKey = null, int? expectedSchemaVersion = null,
        string? defaultDataVersionKey = null, int? expectedDefaultDataVersion = null)
        : base(logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _createIdentityDataCommandClient = createIdentityDataCommandClient;
        _schemaVersionKey = schemaVersionKey;
        _expectedSchemaVersion = expectedSchemaVersion;
        _identityDataVersionKey = identityDataVersionKey;
        _expectedIdentityDataVersion = expectedIdentityDataVersion;
        _defaultDataVersionKey = defaultDataVersionKey;
        _expectedDefaultDataVersion = expectedDefaultDataVersion;
    }

    /// <inheritdoc />
    public async Task EnableAsync(string tenantId)
    {
        if (_schemaVersionKey == null || _expectedSchemaVersion == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var schemaVersion =
            await tenantContext.GetConfigurationAsync(session, _schemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 }).ConfigureAwait(false);
        if (schemaVersion?.Version == _expectedSchemaVersion)
        {
            throw ConfigurationException.TenantAlreadyEnabled(tenantId);
        }

        await ImportCkModelAsync(session, tenantContext).ConfigureAwait(false);

        await tenantContext.SetConfigurationAsync(session, _schemaVersionKey,
            new DefaultConfigurationVersion { Version = _expectedSchemaVersion.Value }).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        await StartTenantAsync(tenantId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisableAsync(string tenantId)
    {
        if (_schemaVersionKey == null || _expectedSchemaVersion == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, _schemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 }).ConfigureAwait(false);
        if (configurationVersion == null || configurationVersion.Version == -1)
        {
            throw ConfigurationException.TenantAlreadyDisabled(tenantId);
        }

        await tenantContext.DeleteConfigurationAsync(session, _schemaVersionKey).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        await StopTenantAsync(tenantId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(string tenantId)
    {
        if (_schemaVersionKey == null || _expectedSchemaVersion == null)
        {
            throw ConfigurationException.TenantCannotBeEnabledDisabled(tenantId);
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, _schemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 }).ConfigureAwait(false);
        if (configurationVersion == null)
        {
            return false;
        }

        return configurationVersion.Version == _expectedSchemaVersion;
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // Check if we need to create the identity data configuration for the services
        await CheckSetupIdentityDataAsync(session, tenantContext).ConfigureAwait(false);

        // Check if we need to import the CK model
        await CheckImportCkModelAsync(session, tenantContext).ConfigureAwait(false);

        // Check if we need to import default data for the service
        await CheckSetupDefaultDataAsync(session, tenantContext).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        await StartTenantAsync(tenantId).ConfigureAwait(false);
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
    /// Method to create or update the default data for the service
    /// </summary>
    /// <param name="session">Admin session</param>
    /// <param name="tenantContext">Tenant context</param>
    /// <returns></returns>
    protected virtual Task CreateUpdateDefaultDataAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // Left intentionally empty for the derived classes to implement
        return Task.CompletedTask;
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

    private async Task CheckImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // Check if we need to import construction kit model
        if (_schemaVersionKey == null || _expectedSchemaVersion == null)
        {
            return;
        }

        _logger.LogInformation("Setting up import ck model for tenant '{TenantId}'", tenantContext.TenantId);

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync<DefaultConfigurationVersion>(session,
                _schemaVersionKey, null).ConfigureAwait(false);
        if (configurationVersion == null)
        {
            await session.CommitTransactionAsync().ConfigureAwait(false);
            return;
        }

        if (configurationVersion.Version < _expectedSchemaVersion)
        {
            _logger.LogInformation("Importing ck model for tenant '{TenantId}'", tenantContext.TenantId);

            await ImportCkModelAsync(session, tenantContext).ConfigureAwait(false);

            await tenantContext.SetConfigurationAsync(session, _schemaVersionKey,
                new DefaultConfigurationVersion { Version = _expectedSchemaVersion.Value }).ConfigureAwait(false);
        }
    }

    private async Task CheckSetupDefaultDataAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // Check if we need to import the default data
        if (_defaultDataVersionKey == null || _expectedDefaultDataVersion == null)
        {
            return;
        }

        _logger.LogInformation("Setting up default data for tenant '{TenantId}'", tenantContext.TenantId);

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync<DefaultConfigurationVersion>(session,
                _defaultDataVersionKey, null).ConfigureAwait(false);
        if (configurationVersion == null)
        {
            await session.CommitTransactionAsync().ConfigureAwait(false);
            return;
        }

        if (configurationVersion.Version < _expectedSchemaVersion)
        {
            _logger.LogInformation("Creating default data for tenant '{TenantId}'", tenantContext.TenantId);

            await CreateUpdateDefaultDataAsync(session, tenantContext).ConfigureAwait(false);

            await tenantContext.SetConfigurationAsync(session, _defaultDataVersionKey,
                new DefaultConfigurationVersion { Version = _expectedDefaultDataVersion.Value }).ConfigureAwait(false);
        }
    }
}