namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Base class for <see cref="IArtifactStore" /> implementations: argument validation helpers and the
///     provider-neutral retention (<see cref="DeleteOlderThanAsync" />) built on list and delete.
/// </summary>
public abstract class ArtifactStoreBase : IArtifactStore
{
    /// <summary>
    ///     Creates the base.
    /// </summary>
    /// <param name="timeProvider">Clock used for retention; <see cref="System.TimeProvider.System" /> when <c>null</c>.</param>
    protected ArtifactStoreBase(TimeProvider? timeProvider)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The clock used for retention.</summary>
    protected TimeProvider TimeProvider { get; }

    /// <inheritdoc />
    public abstract string ProviderName { get; }

    /// <inheritdoc />
    public abstract Task PutAsync(string key, Stream content, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<ArtifactInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract IAsyncEnumerable<ArtifactInfo> ListAsync(string prefix,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<string>> DeleteOlderThanAsync(string prefix, TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        ArtifactKey.ValidatePrefix(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);

        var cutoff = TimeProvider.GetUtcNow() - maxAge;
        var expired = new List<string>();
        await foreach (var info in ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (info.CreatedAt < cutoff)
            {
                expired.Add(info.Key);
            }
        }

        // Delete after listing: deleting while a provider pages through a listing can skip entries.
        var deleted = new List<string>(expired.Count);
        foreach (var key in expired)
        {
            if (await DeleteAsync(key, cancellationToken).ConfigureAwait(false))
            {
                deleted.Add(key);
            }
        }

        return deleted;
    }

    /// <summary>Validates the arguments of <see cref="PutAsync" />.</summary>
    protected static void ValidatePut(string key, Stream content, ArtifactMetadata metadata)
    {
        ArtifactKey.Validate(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream is not readable.", nameof(content));
        }
    }
}
