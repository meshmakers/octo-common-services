using System.Diagnostics;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.AspNetCore.Routing;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>The decision of the tenant check of <see cref="TenantAuthorizationMiddleware" />.</summary>
public enum TenantAuthorizationOutcome
{
    /// <summary>The token tenant equals the route tenant.</summary>
    Match,

    /// <summary>A user token administers a child tenant on a parent-administration endpoint.</summary>
    AllowedParentAdmin,

    /// <summary>A service token of an allow-listed cross-tenant client.</summary>
    AllowedCrossTenantClient,

    /// <summary>Service token in mode <c>Disabled</c>: not checked.</summary>
    NotChecked,

    /// <summary>Mode <c>LogOnly</c>: the token tenant differs from the route tenant; <c>Enforce</c> would refuse it.</summary>
    WouldDeny,

    /// <summary>Mode <c>LogOnly</c>: the token carries no <c>tenant_id</c>; <c>Enforce</c> would refuse it.</summary>
    WouldDenyMissingTenantId,

    /// <summary>Mode <c>Enforce</c>: refused with 403, the token tenant differs from the route tenant.</summary>
    Denied,

    /// <summary>Mode <c>Enforce</c>: refused with 403, the token carries no <c>tenant_id</c>.</summary>
    DeniedMissingTenantId
}

/// <summary>
///     Counter <c>octo.tenant_auth.decisions</c> on meter <see cref="TenantGenerationMetrics.MeterName" />: one
///     measurement per decision of the tenant check of <see cref="TenantAuthorizationMiddleware" />.
/// </summary>
/// <remarks>
///     <para>
///         Labels: <c>outcome</c> (see <see cref="OutcomeLabel" />), <c>token_kind</c> (<c>user</c>, <c>service</c>),
///         <c>mode</c> (the enforcement mode of that token kind: <c>disabled</c>, <c>log_only</c>, <c>enforce</c>),
///         <c>client_id</c> (bounded like <c>octo.tenant_auth.generation_checks</c>) and <c>route</c>. Never the tenant
///         id, the user, <c>sub</c> or a token value.
///     </para>
///     <para>
///         <c>route</c> is the route <b>template</b> of the endpoint (never the request path; <c>unknown</c> without one,
///         <c>other</c> when it is not a usable label value) on the outcomes an operator has to act on —
///         <c>would_deny…</c> and <c>denied…</c>. On every other outcome it is <see cref="AnyRoute" />: allowed requests
///         are the bulk of the traffic, and client × route on them would multiply the series without serving any gate.
///     </para>
///     <para>
///         <b>Series budget.</b> The OpenTelemetry SDK keeps at most 2000 label combinations per metric by default and
///         folds every further one into an overflow series without labels. This class therefore bounds the complete
///         combination, not only <c>client_id</c>: after <see cref="MaxSeries" /> distinct combinations per process, a new
///         combination is recorded with <c>client_id</c> = <c>other</c> and (on refusals) <c>route</c> = <c>other</c>, but
///         keeps <c>outcome</c>, <c>token_kind</c> and <c>mode</c>. Those collapsed combinations are at most 9 × 2 × 3 (their
///         <c>route</c> follows from the outcome), so the total stays below the SDK limit and a refusal is never lost
///         from an outcome-filtered query.
///     </para>
///     <para>
///         Requests the middleware does not judge (anonymous endpoints, no bearer header, cookie principals,
///         unauthenticated requests, routes without a tenant) are not counted. The tenant generation check that follows
///         a passed tenant check has its own counter.
///     </para>
/// </remarks>
public sealed class TenantAuthorizationMetrics : IDisposable
{
    /// <summary>Name of the counter.</summary>
    public const string CounterName = "octo.tenant_auth.decisions";

    /// <summary>Label <c>outcome</c>.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>Label <c>token_kind</c>.</summary>
    public const string TokenKindTag = "token_kind";

    /// <summary>Label <c>mode</c>.</summary>
    public const string ModeTag = "mode";

    /// <summary>Label <c>client_id</c>.</summary>
    public const string ClientIdTag = "client_id";

    /// <summary>Label <c>route</c>.</summary>
    public const string RouteTag = "route";

    /// <summary>Value of <c>token_kind</c> for a token with a subject.</summary>
    public const string UserTokenKind = "user";

    /// <summary>Value of <c>token_kind</c> for a client-credentials token.</summary>
    public const string ServiceTokenKind = "service";

    /// <summary>Value of <c>route</c> when the endpoint has no route template.</summary>
    public const string UnknownRoute = "unknown";

    /// <summary>Value of <c>route</c> when the template is not a usable label value.</summary>
    public const string OtherRoute = "other";

    /// <summary>Value of <c>route</c> on outcomes that are not refusals (<see cref="IsRefusal" />).</summary>
    public const string AnyRoute = "any";

    /// <summary>Value of <c>outcome</c> for an outcome without a label of its own.</summary>
    public const string OtherOutcome = "other";

    /// <summary>Distinct label combinations per process before <c>client_id</c> and <c>route</c> collapse to <c>other</c>.</summary>
    internal const int MaxSeries = 1500;

    private readonly ClientIdLabelBudget _clientIds = new(TenantGenerationMetrics.MaxClientIds);
    private readonly HashSet<(string Outcome, string TokenKind, string Mode, string ClientId, string Route)> _series = [];
    private readonly Lock _seriesGate = new();
    private readonly int _maxSeries;
    private readonly Counter<long> _decisions;
    private readonly Meter _meter;

    /// <summary>Creates the meter and the counter.</summary>
    public TenantAuthorizationMetrics() : this(MaxSeries)
    {
    }

    /// <summary>Creates the meter and the counter with another series budget (tests).</summary>
    internal TenantAuthorizationMetrics(int maxSeries)
    {
        _maxSeries = maxSeries;
        _meter = new Meter(TenantGenerationMetrics.MeterName);
        _decisions = _meter.CreateCounter<long>(CounterName, "{decision}",
            "Tenant check decisions of the tenant authorization middleware, by outcome, token kind, mode, client and route");
    }

    /// <summary>The meter of this instance (tests listen to exactly this one).</summary>
    internal Meter Meter => _meter;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>Counts one decision on a user token.</summary>
    public void RecordUserToken(TenantAuthorizationOutcome outcome, UserTokenTenantEnforcementMode mode,
        string? clientId, Endpoint? endpoint) =>
        Record(outcome, UserTokenKind, ModeLabel(mode), clientId, endpoint);

    /// <summary>Counts one decision on a service token.</summary>
    public void RecordServiceToken(TenantAuthorizationOutcome outcome, ServiceTokenTenantEnforcementMode mode,
        string? clientId, Endpoint? endpoint) =>
        Record(outcome, ServiceTokenKind, ModeLabel(mode), clientId, endpoint);

    /// <summary>The label value of an outcome.</summary>
    public static string OutcomeLabel(TenantAuthorizationOutcome outcome) => outcome switch
    {
        TenantAuthorizationOutcome.Match => "match",
        TenantAuthorizationOutcome.AllowedParentAdmin => "allowed_parent_admin",
        TenantAuthorizationOutcome.AllowedCrossTenantClient => "allowed_cross_tenant_client",
        TenantAuthorizationOutcome.NotChecked => "not_checked",
        TenantAuthorizationOutcome.WouldDeny => "would_deny",
        TenantAuthorizationOutcome.WouldDenyMissingTenantId => "would_deny_missing_tenant_id",
        TenantAuthorizationOutcome.Denied => "denied",
        TenantAuthorizationOutcome.DeniedMissingTenantId => "denied_missing_tenant_id",
        _ => OtherOutcome
    };

    /// <summary>Whether <paramref name="outcome" /> is a refusal, in <c>LogOnly</c> (would be) or in <c>Enforce</c>.</summary>
    public static bool IsRefusal(TenantAuthorizationOutcome outcome) => outcome is TenantAuthorizationOutcome.WouldDeny
        or TenantAuthorizationOutcome.WouldDenyMissingTenantId or TenantAuthorizationOutcome.Denied
        or TenantAuthorizationOutcome.DeniedMissingTenantId;

    /// <summary>The label value of a user-token mode.</summary>
    public static string ModeLabel(UserTokenTenantEnforcementMode mode) =>
        mode == UserTokenTenantEnforcementMode.LogOnly ? "log_only" : "enforce";

    /// <summary>The label value of a service-token mode.</summary>
    public static string ModeLabel(ServiceTokenTenantEnforcementMode mode) => mode switch
    {
        ServiceTokenTenantEnforcementMode.Enforce => "enforce",
        ServiceTokenTenantEnforcementMode.LogOnly => "log_only",
        _ => "disabled"
    };

    /// <summary>The route template of <paramref name="endpoint" />, with a leading slash; never the request path.</summary>
    internal static string RouteLabel(Endpoint? endpoint)
    {
        var template = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(template))
        {
            return UnknownRoute;
        }

        if (template[0] != '/')
        {
            template = "/" + template;
        }

        return MetricLabels.Bounded(template) ?? OtherRoute;
    }

    internal string ClientIdLabel(string? clientId) => _clientIds.Label(clientId);

    private void Record(TenantAuthorizationOutcome outcome, string tokenKind, string mode, string? clientId,
        Endpoint? endpoint)
    {
        var refusal = IsRefusal(outcome);
        var series = (Outcome: OutcomeLabel(outcome), TokenKind: tokenKind, Mode: mode, ClientId: ClientIdLabel(clientId),
            Route: refusal ? RouteLabel(endpoint) : AnyRoute);
        if (!WithinBudget(series))
        {
            series = series with
            {
                ClientId = TenantGenerationMetrics.OtherClientId, Route = refusal ? OtherRoute : AnyRoute
            };
        }

        _decisions.Add(1, new TagList
        {
            { OutcomeTag, series.Outcome },
            { TokenKindTag, series.TokenKind },
            { ModeTag, series.Mode },
            { ClientIdTag, series.ClientId },
            { RouteTag, series.Route }
        });
    }

    /// <summary>Whether the combination is known or still fits into the series budget (then it is remembered).</summary>
    private bool WithinBudget((string Outcome, string TokenKind, string Mode, string ClientId, string Route) series)
    {
        lock (_seriesGate)
        {
            if (_series.Contains(series))
            {
                return true;
            }

            if (_series.Count >= _maxSeries)
            {
                return false;
            }

            _series.Add(series);
            return true;
        }
    }
}
