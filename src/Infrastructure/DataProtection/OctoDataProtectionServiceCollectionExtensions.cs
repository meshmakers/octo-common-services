using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>Options of the persisted Data Protection key ring of a service.</summary>
/// <param name="ApplicationName">Application name of the key ring (isolates it from other services)</param>
public sealed record OctoDataProtectionKeyRingOptions(string ApplicationName);

/// <summary>
///     Registration of a persisted, shared ASP.NET Data Protection key ring for OctoMesh services.
/// </summary>
public static class OctoDataProtectionServiceCollectionExtensions
{
    /// <summary>
    ///     Registers ASP.NET Data Protection with the key ring of <paramref name="applicationName" /> persisted in the
    ///     system tenant of the installation, shared by every replica of the service and kept across restarts, and
    ///     encrypted at rest with the platform key ring (<c>SecretEncryption</c>). Cookies, OpenID Connect state and
    ///     antiforgery tokens written by one replica are then readable by every other one.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="applicationName">
    ///     Application name of the service, unique per service and the same for all its replicas (no whitespace, no
    ///     <c>/</c>). Payloads and keys of different application names are isolated from each other.
    /// </param>
    /// <remarks>
    ///     <para>
    ///     Needs <c>ISystemContext</c> and <c>ISecretAttributeProtector</c> of the runtime engine
    ///     (<c>AddRuntimeEngine()</c>) and the system database admin credentials every engine host receives. Call it
    ///     once per host.
    ///     </para>
    ///     <para>
    ///     Keys are never written unencrypted into the shared store: without a configured platform key ring the shared
    ///     store is not used at all, the service keeps the ASP.NET default (a local key directory, one key ring per
    ///     process) and logs a warning that names the missing setting.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="applicationName" /> is not a valid key ring name</exception>
    public static IServiceCollection AddOctoDataProtectionKeyRing(this IServiceCollection services,
        string applicationName)
    {
        ArgumentNullException.ThrowIfNull(services);
        DataProtectionKeyNames.ValidateApplicationName(applicationName);

        services.AddSingleton(new OctoDataProtectionKeyRingOptions(applicationName));
        services.TryAddSingleton<IDataProtectionKeyRecordStore, SystemTenantDataProtectionKeyRecordStore>();
        services.AddSingleton<OctoDataProtectionKeyRepository>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<IServiceProvider, ISecretAttributeProtector, ILoggerFactory>(
                (options, serviceProvider, secretProtector, loggerFactory) =>
                {
                    var logger = loggerFactory.CreateLogger(typeof(OctoDataProtectionServiceCollectionExtensions));
                    if (!secretProtector.IsConfigured)
                    {
                        logger.LogWarning(
                            "Data Protection key ring of '{ApplicationName}' is NOT shared between replicas: no " +
                            "SecretEncryption key ring is configured (OCTO_SECRETENCRYPTION__KEYS__<id>, " +
                            "OCTO_SECRETENCRYPTION__ACTIVEKEYID), and keys are never stored unencrypted in the shared " +
                            "store. The local default key directory is used instead", applicationName);
                        return;
                    }

                    options.XmlRepository = serviceProvider.GetRequiredService<OctoDataProtectionKeyRepository>();
                    logger.LogInformation(
                        "Data Protection key ring of '{ApplicationName}' is persisted in the system tenant, encrypted " +
                        "with platform key id '{KeyId}'", applicationName, secretProtector.ActiveKeyId);
                });
        services.AddDataProtection().SetApplicationName(applicationName);
        return services;
    }
}
