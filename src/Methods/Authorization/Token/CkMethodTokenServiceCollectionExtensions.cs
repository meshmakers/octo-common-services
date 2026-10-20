using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Opt-in caller token validation for remote method handlers.</summary>
public static class CkMethodTokenServiceCollectionExtensions
{
    /// <summary>
    ///     Makes the remote handler host (<c>AddCkMethodHandlers</c>) validate the access token that every request forwards
    ///     before the handler runs: issuer, signature, expiry, tenant and scope, like the HTTP API of this service, plus an
    ///     optional audience (<see cref="CkMethodTokenOptions.ValidAudiences" />). A call without a valid token is answered
    ///     <c>METHOD_FORBIDDEN</c> (reason <see cref="CkMethodTokenReasons" />) and the handler is not called; a valid one
    ///     reaches the handler with <c>CkMethodContext.CallerPrincipal</c>. The in-process transport is not
    ///     affected: it runs in the process that already authenticated the caller.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    ///     Overrides the configuration section <c>Methods:Token</c>. Typical: <c>o.Authority = &lt;the authority of the HTTP
    ///     API&gt;</c>.
    /// </param>
    public static IServiceCollection AddCkMethodCallerTokenValidation(this IServiceCollection services,
        Action<CkMethodTokenOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<CkMethodTokenOptions>().BindConfiguration(CkMethodTokenOptions.SectionName);
        if (configure != null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CkMethodTokenOptions>,
            CkMethodTokenOptionsValidator>());
        // The tenant rule shares the cross-tenant service client list with the HTTP API (section TenantAuthorization).
        services.AddOptions<TenantAuthorizationOptions>().BindConfiguration(TenantAuthorizationOptions.SectionName);
        services.AddHttpClient(CkMethodTokenOptions.HttpClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICkMethodCallerTokenValidator, CkMethodCallerTokenValidator>();
        return services;
    }
}

internal sealed class CkMethodTokenOptionsValidator(IHostEnvironment environment, ILogger<CkMethodTokenOptionsValidator> logger)
    : IValidateOptions<CkMethodTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, CkMethodTokenOptions options)
    {
        if (options.DisableValidation)
        {
            if (!environment.IsDevelopment())
            {
                return ValidateOptionsResult.Fail(
                    $"{CkMethodTokenOptions.SectionName}:{nameof(CkMethodTokenOptions.DisableValidation)} is a test configuration and " +
                    "is only allowed in the Development environment.");
            }

            logger.LogWarning(
                "Caller token validation for remote method handlers is DISABLED ({Section}:{Setting}); issuer, signature and expiry are not checked",
                CkMethodTokenOptions.SectionName, nameof(CkMethodTokenOptions.DisableValidation));
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.MetadataAddress))
        {
            return ValidateOptionsResult.Fail(
                $"{CkMethodTokenOptions.SectionName}:{nameof(CkMethodTokenOptions.Authority)} is required: set it to the " +
                "authority of the HTTP API of this service.");
        }

        if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.ValidIssuer))
        {
            return ValidateOptionsResult.Fail(
                $"{CkMethodTokenOptions.SectionName}:{nameof(CkMethodTokenOptions.ValidIssuer)} is required when only the " +
                "metadata address is set.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5))
        {
            return ValidateOptionsResult.Fail(
                $"{CkMethodTokenOptions.SectionName}:{nameof(CkMethodTokenOptions.ClockSkew)} must be between 0 and 5 minutes.");
        }

        return ValidateOptionsResult.Success;
    }
}
