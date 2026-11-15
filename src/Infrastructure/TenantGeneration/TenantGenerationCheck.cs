using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     The one tenant generation check (AB#8999, security item AB#8970): does the access token belong to the
///     <b>current</b> tenant with its <c>tenant_id</c>, or to an earlier tenant with the same id that was deleted and
///     created again? Used by <c>TenantAuthorizationMiddleware</c>, the CK method caller checks (bus host and in-process
///     transport) and identity's grants (AB#9017).
/// </summary>
/// <remarks>
///     The generation is always looked up for the tenant the token was <b>issued</b> for (<c>tenant_id</c>), never for
///     the route or call tenant. That covers the REST parent path (a parent token on a child route is checked against the
///     parent) and lets later rules call the check unchanged.
/// </remarks>
public interface ITenantGenerationCheck
{
    /// <summary>
    ///     Evaluates the token's <c>tenant_id</c> and <c>tenant_gen</c> without applying a mode (no log, no metric).
    ///     Never throws for a failed lookup: that is <see cref="TenantGenerationOutcome.LookupFailed" />.
    /// </summary>
    ValueTask<TenantGenerationOutcome> EvaluateAsync(string? tokenTenantId, TenantGenerationClaim? generation,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Evaluates, applies <c>TenantAuthorization:TenantGenerationEnforcement</c>, counts the check and logs
    ///     <c>missing</c> / <c>mismatch</c> (rate-limited per client). In <see cref="TenantGenerationEnforcementMode.Disabled" />
    ///     nothing is looked up, logged or counted.
    /// </summary>
    ValueTask<TenantGenerationCheckResult> CheckAsync(TenantGenerationCheckRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ITenantGenerationCheck" />. See the interface and <c>docs/tenant-generation-check.md</c>.</summary>
/// <remarks>
///     <list type="bullet">
///         <item>No <c>tenant_id</c>, the system tenant (<c>OctoSystemConfiguration.SystemTenantId</c>, never refused, with
///             or without a record and whatever its tokens carry), or a tenant without generation:
///             <see cref="TenantGenerationOutcome.NotChecked" />.</item>
///         <item>The comparison is ordinal on the canonical string of <see cref="TenantGenerationFormat" />; no tolerance,
///             no clock. A claim that is not a single canonical string is a mismatch.</item>
///         <item>A mismatch whose token generation is newer than the cached one refreshes the cache once before it
///             decides (the tenant was recreated after the value was cached).</item>
///     </list>
/// </remarks>
public sealed class TenantGenerationCheck : ITenantGenerationCheck
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);
    private const int MaxLogKeys = 1024;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.Ordinal);
    private readonly ILogger<TenantGenerationCheck> _logger;
    private readonly TenantGenerationMetrics _metrics;
    private readonly IOptions<TenantAuthorizationOptions> _options;
    private readonly ITenantGenerationReader _reader;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the check.</summary>
    public TenantGenerationCheck(ITenantGenerationReader reader, TenantGenerationMetrics metrics,
        IOptions<TenantAuthorizationOptions> options, IOptions<OctoSystemConfiguration> systemConfiguration,
        ILogger<TenantGenerationCheck> logger, TimeProvider? timeProvider = null)
    {
        _reader = reader;
        _metrics = metrics;
        _options = options;
        _systemConfiguration = systemConfiguration;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<TenantGenerationOutcome> EvaluateAsync(string? tokenTenantId,
        TenantGenerationClaim? generation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenTenantId) || IsSystemTenant(tokenTenantId))
        {
            return TenantGenerationOutcome.NotChecked;
        }

        string? current;
        try
        {
            current = await _reader.GetGenerationAsync(tokenTenantId, false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TenantGenerationOutcome.LookupFailed;
        }

        var outcome = Compare(current, generation);
        if (!ShouldRefresh(outcome, current, generation))
        {
            return outcome;
        }

        // The token names a newer generation than the cached one, or the tenant had none when it was cached: the
        // tenant was (re)created after the value was cached. Read once more before deciding.
        try
        {
            current = await _reader.GetGenerationAsync(tokenTenantId, true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TenantGenerationOutcome.LookupFailed;
        }

        return Compare(current, generation);
    }

    /// <inheritdoc />
    public async ValueTask<TenantGenerationCheckResult> CheckAsync(TenantGenerationCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = _options.Value.TenantGenerationEnforcement;
        if (mode == TenantGenerationEnforcementMode.Disabled)
        {
            return new TenantGenerationCheckResult(TenantGenerationOutcome.NotChecked, TenantGenerationDecision.Allow);
        }

        var outcome = await EvaluateAsync(request.TokenTenantId, request.Generation, cancellationToken)
            .ConfigureAwait(false);
        _metrics.Record(outcome, mode, request.Transport, request.ClientId, request.GrantType);

        var decision = (mode, outcome) switch
        {
            (TenantGenerationEnforcementMode.Enforce, TenantGenerationOutcome.Missing or TenantGenerationOutcome.Mismatch)
                => TenantGenerationDecision.Deny,
            (TenantGenerationEnforcementMode.Enforce, TenantGenerationOutcome.LookupFailed)
                => TenantGenerationDecision.Unavailable,
            _ => TenantGenerationDecision.Allow
        };

        if (outcome is TenantGenerationOutcome.Missing or TenantGenerationOutcome.Mismatch)
        {
            Log(request, outcome, mode, decision);
        }

        return new TenantGenerationCheckResult(outcome, decision);
    }

    private bool IsSystemTenant(string tokenTenantId)
    {
        var systemTenantId = _systemConfiguration.Value.SystemTenantId;
        return !string.IsNullOrWhiteSpace(systemTenantId) &&
               string.Equals(tokenTenantId.Trim(), systemTenantId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static TenantGenerationOutcome Compare(string? current, TenantGenerationClaim? generation)
    {
        if (current is null)
        {
            return TenantGenerationOutcome.NotChecked;
        }

        if (generation is null)
        {
            return TenantGenerationOutcome.Missing;
        }

        return generation.IsWellFormed && string.Equals(generation.Value, current, StringComparison.Ordinal)
            ? TenantGenerationOutcome.Match
            : TenantGenerationOutcome.Mismatch;
    }

    private static bool ShouldRefresh(TenantGenerationOutcome outcome, string? current,
        TenantGenerationClaim? generation)
    {
        if (generation is not { IsWellFormed: true } ||
            !TenantGenerationFormat.TryParse(generation.Value, out var tokenGeneration))
        {
            return false;
        }

        return outcome switch
        {
            // identity only stamps a claim for a tenant with a record: the record may have appeared after the value
            // was cached. The reader bounds the re-reads (at most one per tenant per second).
            TenantGenerationOutcome.NotChecked => current is null,
            TenantGenerationOutcome.Mismatch => TenantGenerationFormat.TryParse(current, out var cached) &&
                                               tokenGeneration > cached,
            _ => false
        };
    }

    private void Log(TenantGenerationCheckRequest request, TenantGenerationOutcome outcome,
        TenantGenerationEnforcementMode mode, TenantGenerationDecision decision)
    {
        var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? "<none>" : request.ClientId;
        var key = $"{request.Transport}|{clientId}|{outcome}|{decision}";
        var now = _timeProvider.GetUtcNow();
        if (_lastLogged.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        if (_lastLogged.Count >= MaxLogKeys)
        {
            _lastLogged.Clear();
        }

        _lastLogged[key] = now;

        // Never the tenant_gen value or the token: tenant, client, outcome and mode only.
        if (decision == TenantGenerationDecision.Deny)
        {
            _logger.LogWarning(
                "Denied: access token of client '{ClientId}' for tenant '{TenantId}' failed the tenant generation check " +
                "({Outcome}, transport {Transport}): it was issued for an earlier tenant with the same id or before " +
                "tenant_gen was issued (AB#8999). Further denials of this client are logged at most once a minute",
                clientId, request.TokenTenantId, TenantGenerationMetrics.OutcomeLabel(outcome), request.Transport);
            return;
        }

        _logger.LogWarning(
            "Access token of client '{ClientId}' for tenant '{TenantId}' failed the tenant generation check ({Outcome}, " +
            "transport {Transport}). This would be denied with TenantGenerationEnforcement=Enforce (mode {Mode}, AB#8999). " +
            "Logged at most once a minute per client",
            clientId, request.TokenTenantId, TenantGenerationMetrics.OutcomeLabel(outcome), request.Transport,
            TenantGenerationMetrics.ModeLabel(mode));
    }
}
