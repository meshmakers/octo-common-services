using Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;
using Meshmakers.Octo.Services.ArtifactStorage.HealthChecks;
using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Registers the artifact store.
/// </summary>
public static class ArtifactStorageServiceCollectionExtensions
{
    /// <summary>Default name of the health check registered by <see cref="AddArtifactStorageHealthCheck" />.</summary>
    public const string DefaultHealthCheckName = "artifact-storage";

    /// <summary>
    ///     Binds <see cref="ArtifactStorageOptions" /> from the section <see cref="ArtifactStorageOptions.SectionName" />
    ///     of <paramref name="configuration" /> (environment <c>OCTO_ARTIFACTSTORAGE__…</c>), validates it at start-up
    ///     and registers <see cref="IArtifactStore" /> (singleton, provider chosen by
    ///     <see cref="ArtifactStorageOptions.Provider" />) and <see cref="ArtifactKeyBuilder" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root (not the section).</param>
    /// <param name="configure">Optional post-binding adjustments, e.g. a default root path.</param>
    public static IServiceCollection AddArtifactStorage(this IServiceCollection services,
        IConfiguration configuration, Action<ArtifactStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddOptions<ArtifactStorageOptions>()
            .Bind(configuration.GetSection(ArtifactStorageOptions.SectionName));
        if (configure != null)
        {
            builder.Configure(configure);
        }

        builder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<ArtifactStorageOptions>, ArtifactStorageOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ArtifactKeyBuilder>();
        services.TryAddSingleton(CreateStore);
        return services;
    }

    /// <summary>
    ///     Adds <see cref="ArtifactStoreHealthCheck" />. Requires <see cref="AddArtifactStorage" />.
    /// </summary>
    public static IHealthChecksBuilder AddArtifactStorageHealthCheck(this IHealthChecksBuilder builder,
        string name = DefaultHealthCheckName, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            sp => ActivatorUtilities.CreateInstance<ArtifactStoreHealthCheck>(sp), failureStatus, tags, timeout));
    }

    private static IArtifactStore CreateStore(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<ArtifactStorageOptions>>().Value;
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

        switch (options.Provider)
        {
            case ArtifactStorageProvider.FileSystem:
                var rootPath = string.IsNullOrWhiteSpace(options.FileSystem.RootPath)
                    ? Path.Combine(Path.GetTempPath(), "octo-artifacts")
                    : options.FileSystem.RootPath;
                return new FileSystemArtifactStore(rootPath, timeProvider,
                    loggerFactory?.CreateLogger<FileSystemArtifactStore>());
            case ArtifactStorageProvider.S3:
                return new S3ArtifactStore(options.S3, timeProvider, loggerFactory?.CreateLogger<S3ArtifactStore>());
            case ArtifactStorageProvider.AzureBlob:
                return new AzureBlobArtifactStore(options.AzureBlob, timeProvider,
                    loggerFactory?.CreateLogger<AzureBlobArtifactStore>());
            default:
                throw new InvalidOperationException(
                    $"Unsupported {ArtifactStorageOptions.SectionName}:Provider '{options.Provider}'.");
        }
    }
}
