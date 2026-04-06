namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Arguments for batch import of multiple construction kit models in sequential order.
///     This ensures models are imported one at a time to avoid dependency resolution race conditions.
/// </summary>
public record ImportCkBatchCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">The tenant to import into</param>
    /// <param name="cacheFileKeys">Ordered list of cache file keys to import sequentially</param>
    public ImportCkBatchCommandRequest(string tenantId, List<string> cacheFileKeys)
        : base(tenantId)
    {
        CacheFileKeys = cacheFileKeys;
    }

    /// <summary>
    ///     Returns the ordered list of cache file keys (one per CK model, in dependency order)
    /// </summary>
    public List<string> CacheFileKeys { get; }
}
