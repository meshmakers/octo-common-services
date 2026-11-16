using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Endpoint metadata for an endpoint that serves <b>both</b> tenant content and a few administration operations behind one
///     route (a GraphQL endpoint): it decides <b>per request</b> whether the request consists exclusively of administration
///     operations that an administrator of a parent tenant may invoke on a child tenant.
/// </summary>
/// <remarks>
///     <para>
///         <c>TenantAuthorizationMiddleware</c> asks it only for a <b>user</b> token whose <c>tenant_id</c> differs from the route
///         tenant, and grants the request only when the predicate answers <c>true</c> <b>and</b>
///         <see cref="Services.ITenantHierarchyReader.IsChildTenantAsync" /> confirms that the route tenant is a registered child
///         of the token tenant. Every other request on the endpoint keeps the exact <c>tenant_id</c> match. Service tokens never
///         use it.
///     </para>
///     <para>
///         🔴 This is <b>not</b> <see cref="IAllowParentTenantAdministration" /> and must not derive from it: that marker grants
///         a whole endpoint and must never be put on a GraphQL endpoint. An implementation must fail closed: <c>false</c> for
///         anything it cannot fully classify (a batch, an unknown field, a fragment, a body it cannot read), and <c>true</c> only
///         when <b>every</b> operation of the request is an administration operation. A request that mixes an allowed and a
///         forbidden field is refused as a whole. The operations themselves still check the caller (method rule, record scope).
///     </para>
///     <para>
///         The predicate may read the request body; it must leave it readable for the endpoint (enable buffering and rewind).
///     </para>
/// </remarks>
public interface IAllowParentTenantAdministrationRequest
{
    /// <summary>
    ///     <c>true</c> when the request consists exclusively of administration operations a parent tenant's administrator may
    ///     invoke; <c>false</c> otherwise, including for every request the implementation cannot classify.
    /// </summary>
    /// <param name="context">The request.</param>
    ValueTask<bool> IsParentTenantAdministrationRequestAsync(HttpContext context);
}
