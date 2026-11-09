using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Implements an initialization service to create default configuration for tenants
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DefaultConfigurationInitializationService : IAsyncInitializationService
{
    private readonly ILogger<DefaultConfigurationInitializationService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    /// <param name="defaultConfigurationCreatorService"></param>
    public DefaultConfigurationInitializationService(ILogger<DefaultConfigurationInitializationService> logger, 
        ISystemContext systemContext, IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public int Order => 10;

    /// <summary>
    ///     AB#8677: attempts of the system tenant setup while it fails only because another service is importing or
    ///     upgrading a CK model right now. With <see cref="SystemTenantRetryDelay" /> this bounds the wait to a few
    ///     minutes; a failure that persists after that is no longer a short upgrade window and fails the host start.
    /// </summary>
    internal int MaxSystemTenantAttempts { get; set; } = 30;

    /// <summary>AB#8677: pause between two attempts of the system tenant setup (see <see cref="MaxSystemTenantAttempts" />).</summary>
    internal TimeSpan SystemTenantRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public async Task InitializeAsync()
    {
        // Defer tenant start until the distribution event hub is available.
        // StartTenantAsync may send commands via the bus (e.g. RemoveRecurringJobsByScheduleGroup),
        // so it must run after EventHubStartupService (Order=20) has started the bus.
        // TenantStartupInitializationService (Order=30) will call StartDeferredTenantsAsync.
        _defaultConfigurationCreatorService.DeferTenantStart = true;

        // Do global initialization here
        _logger.LogInformation("Initialize default configuration");
        await _defaultConfigurationCreatorService.InitializeAsync().ConfigureAwait(false);
        _logger.LogInformation("Initialize default configuration done");

        // Call for system tenant. Deliberately NOT guarded like the child-tenant loop below: without the
        // system tenant nothing works at all, so its failure must keep failing the host start.
        _logger.LogInformation("Initialize default configuration for system tenant '{TenantId}'", _systemContext.TenantId);
        await SetupSystemTenantAsync().ConfigureAwait(false);
        _logger.LogInformation("Initialize default configuration for system tenant '{TenantId}' done", _systemContext.TenantId);

        // Call for all child tenants
        _logger.LogInformation("Initialize default configuration for child tenants of '{TenantId}'", _systemContext.TenantId);
        if (await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            // Read the tenant list and close the session/transaction before iterating.
            // Keeping the transaction open while calling SetupAsync for each tenant would
            // cause MongoDB IX lock contention on octosystem.RtEntity_SystemTenant,
            // since SetupAsync also accesses that collection.
            List<OctoTenant> tenantList;
            using (var systemSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false))
            {
                systemSession.StartTransaction();
                var tenants = await _systemContext.GetAllTenantsAsync(systemSession).ConfigureAwait(false);
                tenantList = tenants.Items.ToList();
                await systemSession.CommitTransactionAsync().ConfigureAwait(false);
            }

            // Child tenants are set up in parallel: each tenant's SetupAsync is independent per-tenant
            // work (its own repository, session and CK cache entry — CkCacheService is a per-tenant-keyed
            // ConcurrentDictionary), so the dominant cost — per-tenant CK model migrations and blueprint
            // applies — overlaps instead of running strictly one after another. On a fleet with ~20
            // tenants this cut cold start from minutes of serial setup down to roughly
            // tenants / parallelism. Bounded by the same min(ProcessorCount, 8) the deferred-start lane
            // uses (DefaultConfigurationCreatorServiceStandardized.DeferredTenantStartParallelism), to
            // keep MongoDB connection-pool pressure and CK upgrade cost in check. The system tenant is
            // deliberately set up first and sequentially above — child setup may depend on it.
            var degreeOfParallelism = Math.Min(Environment.ProcessorCount, 8);

            // One tenant must not take the rest down with it (AB#4690). This loop used to be unguarded, so
            // the first tenant whose setup threw aborted it: every remaining tenant was skipped and the
            // whole host start failed. A single broken tenant — e.g. one whose database is briefly
            // unreachable right after a delete + recreate — could therefore leave an entire service
            // unprovisioned or crash-looping. Failures are logged and, for services that wire the retry
            // store, recorded durably by SetupAsync, so the background retry drives them to completion
            // while the service comes up normally for everyone else.
            var failedTenants = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(
                tenantList,
                new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
                async (tenant, _) =>
                {
                    _logger.LogInformation("Initialize default configuration for tenant '{TenantId}'",
                        tenant.TenantId);
                    try
                    {
                        await _defaultConfigurationCreatorService.SetupAsync(tenant.TenantId).ConfigureAwait(false);
                        _logger.LogInformation("Initialize default configuration for tenant '{TenantId}' done",
                            tenant.TenantId);
                    }
                    catch (Exception ex)
                    {
                        failedTenants.Add(tenant.TenantId);
                        _logger.LogError(ex,
                            "Initialize default configuration for tenant '{TenantId}' failed. Continuing with " +
                            "the remaining tenants; this tenant will be retried in the background", tenant.TenantId);
                    }
                }).ConfigureAwait(false);

            if (!failedTenants.IsEmpty)
            {
                _logger.LogWarning(
                    "Default configuration failed for {Count} of {Total} tenant(s): {Tenants}",
                    failedTenants.Count, tenantList.Count, string.Join(", ", failedTenants));
            }
        }
    }

    /// <summary>
    ///     Sets up the system tenant. AB#8677: when another service imports or upgrades a CK model in place at the same
    ///     moment (a rolling update, or the whole stack starting at once), a dependency on that model has no Available
    ///     version for a short time and the embedded CK import or the CK cache load of this setup fails. The engine
    ///     marks such a failure; it is retried here instead of stopping the host. Every other failure still fails the
    ///     host start at once.
    /// </summary>
    private async Task SetupSystemTenantAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _defaultConfigurationCreatorService.SetupAsync(_systemContext.TenantId).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxSystemTenantAttempts &&
                                       CkModelImportInProgressClassifier.IsCkModelImportInProgress(ex))
            {
                _logger.LogWarning(
                    "Setup of system tenant '{TenantId}' failed because CK model(s) '{CkModels}' are being imported " +
                    "by another service; retrying in {Delay} (attempt {Attempt} of {MaxAttempts}): {Error}",
                    _systemContext.TenantId, CkModelImportInProgressClassifier.DescribeModels(ex),
                    SystemTenantRetryDelay, attempt, MaxSystemTenantAttempts, ex.Message);
                await Task.Delay(SystemTenantRetryDelay).ConfigureAwait(false);
            }
        }
    }
}