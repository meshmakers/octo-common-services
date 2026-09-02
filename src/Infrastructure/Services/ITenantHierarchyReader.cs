namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Answers parent/child questions about the tenant hierarchy, cached, for callers that sit in the
///     request path (AB#5060 — the parent-tenant administration rule of
///     <c>TenantAuthorizationMiddleware</c>, which applies on endpoints marked
///     <c>[AllowParentTenantAdministration]</c>).
/// </summary>
/// <remarks>
///     <para>
///         The hierarchy is navigable <b>downwards only</b>: every tenant's own database holds one
///         registry record per <i>direct</i> child, and the system tenant's database additionally holds
///         one record for every tenant in the estate. There is no published API that returns a tenant's
///         parent, so "is A an ancestor of B" can only be answered by descending from A — which is why
///         this interface asks the one question that costs a single indexed lookup.
///     </para>
///     <para>
///         Implementations must be safe to call on every request: cheap, cached, and fail-closed. A
///         hierarchy that cannot be read is <c>false</c>, never an exception bubbling into a 500.
///     </para>
/// </remarks>
public interface ITenantHierarchyReader
{
    /// <summary>
    ///     Whether <paramref name="tenantId" /> is registered as a child of
    ///     <paramref name="parentTenantId" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reach is <b>one level</b> for an ordinary parent tenant. For the <b>system</b> tenant the
    ///         registry consulted is the platform-wide one, so the answer covers the whole estate —
    ///         which is the correct descendant answer there, the system tenant being the root of the
    ///         hierarchy.
    ///     </para>
    ///     <para>
    ///         A tenant is never its own child: equal ids answer <c>false</c> without any lookup.
    ///         Comparison is case-insensitive, like every other tenant id comparison on this path.
    ///     </para>
    /// </remarks>
    /// <param name="parentTenantId">The tenant whose registry is consulted.</param>
    /// <param name="tenantId">The tenant to look for.</param>
    Task<bool> IsChildTenantAsync(string parentTenantId, string tenantId);
}
