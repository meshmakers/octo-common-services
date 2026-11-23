using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     Registration of <see cref="IBlueprintCapabilityReconciler" />.
/// </summary>
public static class BlueprintCapabilityServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="IBlueprintCapabilityReconciler" /> (scoped, like <see cref="Services.IConfigurationService" />)
    ///     and binds <see cref="BlueprintCapabilityOptions" /> from the section
    ///     <see cref="BlueprintCapabilityOptions.SectionName" /> when a configuration is given. The host must register
    ///     the blueprint installation store (<c>AddMongoBlueprintSupport</c>), otherwise the engine's in-memory store
    ///     answers and no capability is ever enabled.
    /// </summary>
    public static IServiceCollection AddBlueprintCapabilityReconciler(this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<BlueprintCapabilityOptions>();
        if (configuration != null)
        {
            optionsBuilder.Bind(configuration.GetSection(BlueprintCapabilityOptions.SectionName));
        }

        services.TryAddScoped<IBlueprintCapabilityReconciler, BlueprintCapabilityReconciler>();
        return services;
    }
}
