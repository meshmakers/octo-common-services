using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.ArtifactStorage.HealthChecks;

/// <summary>
///     Checks that the artifact store is reachable: lists <c>&lt;InstancePrefix&gt;/_health/</c>, and with
///     <see cref="ArtifactStorageOptions.HealthCheckWriteProbe" /> also writes, reads back and deletes a probe object.
/// </summary>
public sealed class ArtifactStoreHealthCheck : IHealthCheck
{
    /// <summary>The key segment below the instance prefix that holds probe objects.</summary>
    public const string HealthSegment = "_health";

    private readonly IOptions<ArtifactStorageOptions> _options;
    private readonly IArtifactStore _store;

    /// <summary>Creates the health check.</summary>
    public ArtifactStoreHealthCheck(IArtifactStore store, IOptions<ArtifactStorageOptions> options)
    {
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var prefix = $"{options.InstancePrefix}/{HealthSegment}/";
        var data = new Dictionary<string, object> { ["provider"] = _store.ProviderName };
        try
        {
            await foreach (var _ in _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            if (options.HealthCheckWriteProbe)
            {
                var key = $"{prefix}{Guid.NewGuid():N}.probe";
                using (var content = new MemoryStream([1, 2, 3, 4], false))
                {
                    await _store.PutAsync(key, content, ArtifactMetadata.None, cancellationToken)
                        .ConfigureAwait(false);
                }

                var info = await _store.GetInfoAsync(key, cancellationToken).ConfigureAwait(false);
                await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                if (info is not { Size: 4 })
                {
                    return new HealthCheckResult(context.Registration.FailureStatus,
                        "The artifact store did not return the probe object that was written.", data: data);
                }
            }

            return HealthCheckResult.Healthy($"Artifact store ({_store.ProviderName}) is reachable.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The exception message of the storage SDKs does not contain credentials.
            return new HealthCheckResult(context.Registration.FailureStatus,
                $"Artifact store ({_store.ProviderName}) is not reachable.", e, data);
        }
    }
}
