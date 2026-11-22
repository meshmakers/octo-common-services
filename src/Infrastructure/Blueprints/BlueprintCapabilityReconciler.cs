using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     Default <see cref="IBlueprintCapabilityReconciler" />: checks the event against the tenant (tenant registry,
///     installed blueprint) and enables the capability through the hosting service's <see cref="IConfigurationService" />.
///     The invariant is on the interface; the rules are in <c>docs/blueprint-capabilities.md</c>.
/// </summary>
public sealed class BlueprintCapabilityReconciler : IBlueprintCapabilityReconciler
{
    private readonly ISystemContext _systemContext;
    private readonly ITenantBlueprintInstallations _installations;
    private readonly IConfigurationService _configurationService;
    private readonly IOptions<BlueprintCapabilityOptions> _options;
    private readonly ILogger<BlueprintCapabilityReconciler> _logger;

    /// <summary>Creates a new instance.</summary>
    public BlueprintCapabilityReconciler(ISystemContext systemContext, ITenantBlueprintInstallations installations,
        IConfigurationService configurationService, IOptions<BlueprintCapabilityOptions> options,
        ILogger<BlueprintCapabilityReconciler> logger)
    {
        _systemContext = systemContext;
        _installations = installations;
        _configurationService = configurationService;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BlueprintCapabilityOutcome> ReconcileAsync(BlueprintCapabilityRequest request,
        BlueprintCapability capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CkModelDependencies == null)
        {
            _logger.LogDebug(
                "{Trigger} for blueprint '{BlueprintId}' on tenant '{TenantId}' carries no CK model dependencies (older publisher); {Capability} is not derived from it",
                request.Trigger, request.BlueprintId, request.TenantId, capability);
            return BlueprintCapabilityOutcome.NoDependencyInformation;
        }

        if (!BlueprintCapabilityRequirements.IsRequired(capability, request.CkModelDependencies))
        {
            return BlueprintCapabilityOutcome.NotRequired;
        }

        string? resolvedTenantId = null;
        try
        {
            var (tenantId, refusal) = await ResolveInstalledTenantAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (tenantId == null)
            {
                _logger.LogWarning(
                    "{Capability} is not enabled after {Trigger} for blueprint '{BlueprintId}' on tenant '{TenantId}' (correlation {CorrelationId}): {Reason}",
                    capability, request.Trigger, request.BlueprintId, request.TenantId, request.CorrelationId,
                    refusal);
                return BlueprintCapabilityOutcome.Refused;
            }

            resolvedTenantId = tenantId;

            if (await _configurationService.IsEnabledAsync(tenantId).ConfigureAwait(false))
            {
                _logger.LogDebug("{Capability} is already enabled for tenant '{TenantId}'", capability, tenantId);
                return BlueprintCapabilityOutcome.AlreadyEnabled;
            }

            if (capability == BlueprintCapability.Ai &&
                !await WaitForCommunicationAsync(tenantId, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "AI is not enabled for tenant '{TenantId}' after {Trigger} for blueprint '{BlueprintId}' (correlation {CorrelationId}): Communication is not enabled for the tenant; run EnableCommunication and EnableAi",
                    tenantId, request.Trigger, request.BlueprintId, request.CorrelationId);
                return BlueprintCapabilityOutcome.PrerequisiteMissing;
            }

            await _configurationService.EnableAsync(tenantId).ConfigureAwait(false);

            // Information, not Warning: every tenant created with a starter blueprint gets here. A re-enable after a
            // manual disable cannot be told apart and is logged the same way; failures to enable stay warnings/errors.
            _logger.LogInformation(
                "{Capability} enabled for tenant '{TenantId}' by the platform: blueprint '{BlueprintId}' needs it ({Trigger}, correlation {CorrelationId})",
                capability, tenantId, request.BlueprintId, request.Trigger, request.CorrelationId);
            return BlueprintCapabilityOutcome.Enabled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The flag is on although the enable threw: another replica of the service enabled it meanwhile
            // (broadcast consumers), or the enable wrote the flag and a later step (tenant start, refresh) failed.
            // Both cannot be told apart here, so the cause is logged and never swallowed.
            if (await IsEnabledQuietlyAsync(resolvedTenantId).ConfigureAwait(false))
            {
                _logger.LogWarning(e,
                    "{Capability} is enabled for tenant '{TenantId}' after {Trigger} for blueprint '{BlueprintId}' (correlation {CorrelationId}), but the enable reported an error: {Cause}. If no other replica enabled it, check that the capability works for the tenant",
                    capability, resolvedTenantId, request.Trigger, request.BlueprintId, request.CorrelationId,
                    e.Message);
                return BlueprintCapabilityOutcome.AlreadyEnabled;
            }

            _logger.LogError(e,
                "{Capability} could not be enabled for tenant '{TenantId}' after {Trigger} for blueprint '{BlueprintId}' (correlation {CorrelationId}): {Cause}. The blueprint stays installed; enable the capability by hand",
                capability, resolvedTenantId ?? request.TenantId, request.Trigger, request.BlueprintId,
                request.CorrelationId, e.Message);
            return BlueprintCapabilityOutcome.Failed;
        }
    }

    /// <summary>
    ///     Resolves the tenant of the event and checks that the blueprint of the event is installed there in exactly that
    ///     version. Retries while the tenant or the installation row is not visible yet: a tenant created with a blueprint
    ///     publishes the event before the tenant registry commits.
    /// </summary>
    private async Task<(string? TenantId, string Refusal)> ResolveInstalledTenantAsync(
        BlueprintCapabilityRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return (null, "the event names no tenant");
        }

        // Name-Version, as the engine publishes it (BlueprintId.FullName); the lenient BlueprintId parser would
        // default a missing version to 1.0.0.
        if (string.IsNullOrWhiteSpace(request.BlueprintId) || request.BlueprintId.IndexOf('-', StringComparison.Ordinal) <= 0)
        {
            return (null, $"'{request.BlueprintId}' is no valid blueprint id");
        }

        BlueprintId blueprintId;
        try
        {
            blueprintId = new BlueprintId(request.BlueprintId);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            return (null, $"'{request.BlueprintId}' is no valid blueprint id");
        }

        var options = _options.Value;
        var attempts = Math.Max(1, options.LookupAttempts);
        var lookupDelay = NonNegative(options.LookupDelay);
        var lastReason = "the tenant does not exist";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(lookupDelay, cancellationToken).ConfigureAwait(false);
            }

            ITenantRepository? tenantRepository;
            try
            {
                tenantRepository = await _systemContext.TryFindTenantRepositoryAsync(request.TenantId)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastReason = $"the tenant could not be resolved: {e.Message}";
                continue;
            }

            if (tenantRepository == null)
            {
                lastReason = "the tenant does not exist";
                continue;
            }

            var tenantId = tenantRepository.TenantId;
            if (string.Equals(tenantId, _systemContext.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "capabilities are never enabled for the system tenant by a blueprint");
            }

            BlueprintInstallation? installation;
            try
            {
                installation = await _installations
                    .GetByBlueprintNameAsync(tenantId, blueprintId.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastReason = $"the blueprint installations of the tenant could not be read: {e.Message}";
                continue;
            }

            if (installation == null)
            {
                lastReason = $"blueprint '{blueprintId.Name}' is not installed in the tenant";
                continue;
            }

            if (!string.Equals(installation.BlueprintId.FullName, blueprintId.FullName, StringComparison.Ordinal))
            {
                // The row is written before the event is published, so another version is no timing issue.
                return (null,
                    $"the tenant runs '{installation.BlueprintId.FullName}', not '{blueprintId.FullName}'");
            }

            return (tenantId, string.Empty);
        }

        return (null, $"{lastReason} (checked {attempts} time(s))");
    }

    private async Task<bool> WaitForCommunicationAsync(string tenantId, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var attempts = Math.Max(1, options.PrerequisiteAttempts);
        var prerequisiteDelay = NonNegative(options.PrerequisiteDelay);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(prerequisiteDelay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);
                using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
                var communication = await tenantContext.GetConfigurationAsync(session,
                        TenantCapabilityConfigurationKeys.Communication,
                        new DefaultConfigurationEnabled { IsEnabled = false })
                    .ConfigureAwait(false);
                if (communication is { IsEnabled: true })
                {
                    return true;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Reading the Communication flag of tenant '{TenantId}' failed; retrying",
                    tenantId);
            }
        }

        return false;
    }

    // A negative delay from configuration (-1 ms is Timeout.InfiniteTimeSpan) would block the consumer for good or
    // throw on every event; it counts as no delay.
    private static TimeSpan NonNegative(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

    private async Task<bool> IsEnabledQuietlyAsync(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        try
        {
            return await _configurationService.IsEnabledAsync(tenantId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
