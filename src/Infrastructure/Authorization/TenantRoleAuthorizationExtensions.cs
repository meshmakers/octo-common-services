using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     The one place that defines "full-access scope plus a tenant role" (AB#7319, lifted from the asset repository's
///     <c>TenantAdministrationPolicy</c>, AB#4497).
/// </summary>
public static class TenantRoleAuthorizationExtensions
{
    /// <summary>
    ///     Adds a policy that requires the scope <see cref="CommonConstants.OctoApiFullAccess" /> and the tenant role
    ///     <paramref name="roleName" />. Requires <see cref="AddOctoTenantRoleAuthorization" /> on the service collection; without
    ///     it the requirement has no handler and every request is refused (fail closed).
    /// </summary>
    public static AuthorizationOptions AddTenantRolePolicy(this AuthorizationOptions options, string policyName,
        string roleName)
    {
        options.AddPolicy(policyName, policy => policy.AddRequirements(new TenantRoleRequirement(roleName)));
        return options;
    }

    /// <summary>
    ///     Registers the handler of <see cref="TenantRoleRequirement" /> and the result handler that puts the reason into the
    ///     <c>403</c> body (AB#5227 format).
    /// </summary>
    public static IServiceCollection AddOctoTenantRoleAuthorization(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, TenantRoleAuthorizationHandler>());
        services.Replace(ServiceDescriptor
            .Singleton<IAuthorizationMiddlewareResultHandler, ForbiddenReasonAuthorizationResultHandler>());
        return services;
    }
}
