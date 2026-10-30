using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the CK method audit (F3.5, AB#6919).</summary>
public static class CkMethodAuditServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="ICkMethodAuditRecorder" /> (the gateway can use it without the pipeline) and the outermost
    ///     audit filter <see cref="CkMethodAuditFilter" /> (band 100). The recorder writes to the registered
    ///     <c>IAuditEventSink</c> (the event log, after <c>AddNotificationServices</c>). Safe to call more than once; the filter is
    ///     added once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional override of the configuration section <c>Methods:Audit</c>.</param>
    public static IServiceCollection AddCkMethodAudit(this IServiceCollection services,
        Action<CkMethodAuditOptions>? configure = null)
    {
        var options = services.AddOptions<CkMethodAuditOptions>().BindConfiguration(CkMethodAuditOptions.SectionName);
        if (configure != null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<ICkMethodPrincipalProvider, CkMethodPrincipalScope>();
        services.TryAddSingleton<ICkMethodAuditRecorder, CkMethodAuditRecorder>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodAuditFilter)))
        {
            services.AddCkMethodInvocationFilter<CkMethodAuditFilter>();
        }

        return services;
    }
}
