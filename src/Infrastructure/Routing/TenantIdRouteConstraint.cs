using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Meshmakers.Octo.Services.Infrastructure.Routing;

/// <summary>
///     The <c>{tenantId:tenantId}</c> route constraint every tenant-serving OctoMesh host registers.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>This is a routing constraint, not an authorization check.</b> It decides whether a
///         path segment can be a tenant id at all; whether the caller may address <i>that</i> tenant
///         is <see cref="Middleware.TenantAuthorizationMiddleware" />'s job, and whether the tenant
///         exists is the resolving service's. Do not add lookups here — the constraint runs during
///         route matching, before authentication, on every request.
///     </para>
///     <para>
///         <b>Why it accepts exactly this character set.</b> It is the same rule tenant <i>creation</i>
///         enforces (<c>TenantContext.ValidateTenantIdFormat</c>: at most 64 ASCII letters, digits,
///         <c>-</c> or <c>_</c>), so it cannot reject a tenant that could exist. Keeping the two in
///         step is the point — a laxer constraint would let a segment that can never name a tenant
///         travel deeper into services that then use it as a Mongo database name, a cache key or a
///         directory name, and a stricter one would 404 a real tenant.
///     </para>
///     <para>
///         🔴 <b>Consolidated from seven copies (AB#5060).</b> Every tenant-serving host carried its
///         own <c>internal</c> class, and they had drifted: five accepted any non-null value —
///         including whitespace and dot segments — one added an empty <c>if</c> block, one wrote an
///         untyped <c>HttpContext.Items["d"]</c> that nothing read, and only one checked for an empty
///         string. Divergence in a security-adjacent primitive is the failure mode worth removing
///         here: the copies were never meant to differ, and nothing made it visible that they did.
///     </para>
/// </remarks>
public sealed class TenantIdRouteConstraint : IRouteConstraint
{
    /// <summary>
    ///     Mirrors <c>TenantContext.MaxTenantIdLength</c>. Duplicated rather than referenced because
    ///     this assembly deliberately does not depend on the runtime engine; the doc above is the
    ///     link, and <c>TenantIdRouteConstraintTests</c> pins the shared boundary.
    /// </summary>
    public const int MaxTenantIdLength = 64;

    /// <inheritdoc />
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var value) || value is not string tenantId)
        {
            return false;
        }

        return IsWellFormed(tenantId);
    }

    /// <summary>
    ///     Whether <paramref name="tenantId" /> can name a tenant. Public so a caller that builds a
    ///     tenant route rather than receiving one can ask the same question.
    /// </summary>
    /// <param name="tenantId">The candidate tenant id.</param>
    /// <returns><c>true</c> when it is a well-formed tenant id.</returns>
    public static bool IsWellFormed(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId) || tenantId.Length > MaxTenantIdLength)
        {
            return false;
        }

        foreach (var c in tenantId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
