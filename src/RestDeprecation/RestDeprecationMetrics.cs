using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     The meter of the REST deprecation mechanism (AB#8421): <c>octo.rest.deprecated.calls</c>, the remaining calls of
///     deprecated REST operations per endpoint and client. It gates the removal of an operation (F6.2).
/// </summary>
/// <remarks>
///     <b>Labels, and what they never contain.</b> <c>endpoint</c> is the HTTP method plus the route <b>template</b>
///     (<c>GET {tenantId}/v1/users/{id}</c>), never the concrete path: the path carries tenant ids and user names.
///     <c>client_id</c> is the <c>client_id</c> claim of the authenticated caller, <c>anonymous</c> without an
///     authenticated principal; the pipeline service accounts <c>octo-pipeline-sa-{adapterRtId}</c> (adapter-bound) and
///     <c>octo-pipeline-sa-{configurationRtId}</c> (standalone, <c>PipelineServiceAccountProvisioningService.BuildClientId</c>)
///     carry an entity id in their client id and are all reported as <c>octo-pipeline-sa</c>; a standalone account with a
///     custom client id outside the prefix is reported as itself. <c>status_class</c> is <c>1xx</c> … <c>5xx</c>. No tenant, user name, <c>sub</c>,
///     parameter or token — the same rule as <c>Meshmakers.Octo.Methods</c>. The values are bounded: the endpoint set is
///     fixed in code, and after <see cref="MaxClientIds" /> distinct client ids further ones are counted as
///     <c>other</c>.
/// </remarks>
public static class RestDeprecationMetrics
{
    /// <summary>Name of the meter; registered by <c>AddObservability()</c> in every service.</summary>
    public const string MeterName = "Meshmakers.Octo.RestDeprecation";

    /// <summary>Name of the counter.</summary>
    public const string CallsName = "octo.rest.deprecated.calls";

    internal const string EndpointTag = "endpoint";
    internal const string ClientIdTag = "client_id";
    internal const string StatusClassTag = "status_class";

    internal const string Anonymous = "anonymous";
    internal const string Other = "other";
    internal const string AnyMethod = "ANY";
    internal const string ClientIdClaim = "client_id";

    /// <summary>
    ///     Prefix of the pipeline service accounts (<c>octo-pipeline-sa-{adapterRtId}</c> or, standalone,
    ///     <c>octo-pipeline-sa-{configurationRtId}</c>); their id carries a runtime id, which is entity data and unbounded,
    ///     so they share the single label value <see cref="PipelineServiceAccountLabel" />. Custom client ids outside the
    ///     prefix are not matched.
    /// </summary>
    internal const string PipelineServiceAccountPrefix = "octo-pipeline-sa-";

    /// <summary>Label value of every pipeline service account.</summary>
    internal const string PipelineServiceAccountLabel = "octo-pipeline-sa";

    /// <summary>Upper bound of distinct <c>client_id</c> label values per process.</summary>
    internal const int MaxClientIds = 1000;

    /// <summary>Longest client id reported as itself; longer values are reported as <c>other</c>.</summary>
    internal const int MaxClientIdLength = 200;

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Calls = Meter.CreateCounter<long>(CallsName,
        description: "Calls of deprecated REST operations by endpoint (method + route template), client id and status class");

    private static readonly ConcurrentDictionary<string, byte> KnownClientIds = new(StringComparer.Ordinal);

    /// <summary>Counts one call of a deprecated operation.</summary>
    internal static void RecordCall(HttpContext context, Endpoint endpoint)
    {
        Calls.Add(1, new TagList
        {
            { EndpointTag, EndpointLabel(context.Request.Method, endpoint) },
            { ClientIdTag, ClientIdLabel(context.User) },
            { StatusClassTag, StatusClassLabel(context.Response.StatusCode) }
        });
    }

    /// <summary>HTTP method plus route template; the concrete path is never used.</summary>
    internal static string EndpointLabel(string requestMethod, Endpoint endpoint)
    {
        var template = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(template))
        {
            // No route template to report; the display name is code, not request data.
            template = endpoint.DisplayName ?? Other;
        }
        else if (template[0] != '/')
        {
            template = "/" + template;
        }

        // An endpoint restricted to HTTP methods only matches those, so the request method is bounded. An endpoint
        // without the restriction matches any method string a caller sends, which must not become a label value.
        var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is { HttpMethods.Count: > 0 }
            ? requestMethod.ToUpperInvariant()
            : AnyMethod;

        return method + " " + template;
    }

    /// <summary>The <c>client_id</c> claim of the authenticated caller, <c>anonymous</c> otherwise.</summary>
    internal static string ClientIdLabel(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Anonymous;
        }

        var clientId = user.FindFirst(ClientIdClaim)?.Value;
        if (string.IsNullOrEmpty(clientId))
        {
            return Anonymous;
        }

        // Before the length check and the cap: the tenant must never reach the label, and the accounts use no cap slot.
        if (clientId.StartsWith(PipelineServiceAccountPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PipelineServiceAccountLabel;
        }

        if (clientId.Length > MaxClientIdLength)
        {
            return Other;
        }

        if (KnownClientIds.ContainsKey(clientId))
        {
            return clientId;
        }

        // not atomic against a burst; the bound holds up to the number of racing threads, which is fine for a safety valve
        if (KnownClientIds.Count >= MaxClientIds)
        {
            return Other;
        }

        KnownClientIds.TryAdd(clientId, 0);
        return clientId;
    }

    /// <summary><c>1xx</c> … <c>5xx</c>; anything outside 100–599 is <c>other</c>.</summary>
    internal static string StatusClassLabel(int statusCode)
    {
        return statusCode switch
        {
            >= 100 and < 200 => "1xx",
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => Other
        };
    }
}
