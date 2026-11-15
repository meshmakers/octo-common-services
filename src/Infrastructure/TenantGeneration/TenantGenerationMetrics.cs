using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     Counter <c>octo.tenant_auth.generation_checks</c> on meter <see cref="MeterName" /> (AB#8999).
/// </summary>
/// <remarks>
///     <para>
///         Labels: <c>outcome</c> (<c>match</c>, <c>missing</c>, <c>mismatch</c>, <c>not_checked</c>,
///         <c>lookup_failed</c>), <c>mode</c> (<c>log_only</c>, <c>enforce</c>), <c>transport</c> (<c>http</c>,
///         <c>method</c>, <c>grant</c>), <c>client_id</c>, and <c>grant_type</c> only when the caller passes one (identity,
///         AB#9017). Never the tenant id, the user, <c>sub</c>, <c>tenant_gen</c> or a token value.
///     </para>
///     <para>
///         <c>client_id</c> comes from a validated token, i.e. it is a client registered at identity; a missing or
///         malformed value is <c>unknown</c>, and after <see cref="MaxClientIds" /> distinct values per process further ones
///         are counted as <c>other</c>, so the series stay bounded.
///     </para>
///     <para>Registered in <c>ObservabilityBuilder</c> by this name: an unregistered meter drops every measurement (AB#5430).</para>
/// </remarks>
public sealed class TenantGenerationMetrics : IDisposable
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "Meshmakers.Octo.TenantAuthorization";

    /// <summary>Name of the counter.</summary>
    public const string CounterName = "octo.tenant_auth.generation_checks";

    /// <summary>Label names, the complete set.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>Label <c>mode</c>.</summary>
    public const string ModeTag = "mode";

    /// <summary>Label <c>transport</c>.</summary>
    public const string TransportTag = "transport";

    /// <summary>Label <c>client_id</c>.</summary>
    public const string ClientIdTag = "client_id";

    /// <summary>Label <c>grant_type</c> (identity only).</summary>
    public const string GrantTypeTag = "grant_type";

    /// <summary>The value of <c>client_id</c> when the token carries none or an unusable one.</summary>
    public const string UnknownClientId = "unknown";

    /// <summary>The value of <c>client_id</c> after the budget of distinct values is used up.</summary>
    public const string OtherClientId = "other";

    internal const int MaxClientIds = 1000;
    private const int MaxLabelLength = 128;

    private readonly HashSet<string> _clientIds = new(StringComparer.Ordinal);
    private readonly Lock _clientIdsGate = new();
    private readonly Counter<long> _checks;
    private readonly Meter _meter;

    /// <summary>Creates the meter and the counter.</summary>
    public TenantGenerationMetrics()
    {
        _meter = new Meter(MeterName);
        _checks = _meter.CreateCounter<long>(CounterName, "{check}",
            "Tenant generation (tenant_gen) checks of access tokens, by outcome");
    }

    /// <summary>The meter of this instance (tests listen to exactly this one).</summary>
    internal Meter Meter => _meter;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>Counts one check.</summary>
    public void Record(TenantGenerationOutcome outcome, TenantGenerationEnforcementMode mode, string transport,
        string? clientId, string? grantType = null)
    {
        var tags = new TagList
        {
            { OutcomeTag, OutcomeLabel(outcome) },
            { ModeTag, ModeLabel(mode) },
            { TransportTag, Bounded(transport) ?? "other" },
            { ClientIdTag, ClientIdLabel(clientId) }
        };
        if (!string.IsNullOrWhiteSpace(grantType))
        {
            tags.Add(GrantTypeTag, Bounded(grantType) ?? "other");
        }

        _checks.Add(1, tags);
    }

    /// <summary>The label value of an outcome.</summary>
    public static string OutcomeLabel(TenantGenerationOutcome outcome) => outcome switch
    {
        TenantGenerationOutcome.Match => "match",
        TenantGenerationOutcome.Missing => "missing",
        TenantGenerationOutcome.Mismatch => "mismatch",
        TenantGenerationOutcome.NotChecked => "not_checked",
        _ => "lookup_failed"
    };

    /// <summary>The label value of a mode.</summary>
    public static string ModeLabel(TenantGenerationEnforcementMode mode) => mode switch
    {
        TenantGenerationEnforcementMode.Enforce => "enforce",
        TenantGenerationEnforcementMode.LogOnly => "log_only",
        _ => "disabled"
    };

    internal string ClientIdLabel(string? clientId)
    {
        var bounded = Bounded(clientId);
        if (bounded is null)
        {
            return UnknownClientId;
        }

        lock (_clientIdsGate)
        {
            if (_clientIds.Contains(bounded))
            {
                return bounded;
            }

            if (_clientIds.Count >= MaxClientIds)
            {
                return OtherClientId;
            }

            _clientIds.Add(bounded);
            return bounded;
        }
    }

    private static string? Bounded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLabelLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (c < 0x21 || c > 0x7e)
            {
                return null;
            }
        }

        return value;
    }
}
