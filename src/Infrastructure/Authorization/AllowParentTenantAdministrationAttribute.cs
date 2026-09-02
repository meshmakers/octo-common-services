namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Endpoint metadata marking an operation that <b>administers</b> the addressed tenant and may
///     therefore be invoked by an administrator of a tenant <i>above</i> it (AB#5060).
/// </summary>
/// <remarks>
///     <para>
///         <c>TenantAuthorizationMiddleware</c> looks this up through the endpoint metadata, the same
///         way it already honours <c>[AllowAnonymous]</c>. Implement it on an attribute (see
///         <see cref="AllowParentTenantAdministrationAttribute" />) for MVC controllers, or add any
///         implementation as metadata for minimal APIs.
///     </para>
///     <para>
///         🔴 <b>Administration, not access.</b> A parent administrator may back up, restore or
///         export a child tenant; they may <b>not</b> read or write that tenant's data. This marker
///         therefore belongs on tenant lifecycle and archive operations only — never on a GraphQL
///         endpoint, an entity route, a query route or anything else that returns tenant content.
///         Every unmarked endpoint keeps the exact <c>tenant_id</c> match unchanged.
///     </para>
/// </remarks>
public interface IAllowParentTenantAdministration;

/// <summary>
///     Marks a controller or action as an administration operation on the addressed tenant, which an
///     administrator of a parent tenant may invoke even though their token was issued for the parent
///     (AB#5060). See <see cref="IAllowParentTenantAdministration" /> for the rule and its limits.
/// </summary>
/// <remarks>
///     🔴 Read <see cref="IAllowParentTenantAdministration" /> before adding this to an endpoint: it
///     widens who may call the endpoint, and it must never appear on an endpoint that reads or writes
///     the tenant's data.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class AllowParentTenantAdministrationAttribute : Attribute, IAllowParentTenantAdministration;
