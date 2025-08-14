using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Arguments for import runtime data
/// </summary>
public record ImportRtCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="importStrategy">Import strategy to use</param>
    /// <param name="cacheFileKey">Cache file key</param>
    public ImportRtCommandRequest(string tenantId, ImportStrategy importStrategy, string cacheFileKey)
        : base(tenantId)
    {
        ImportStrategy = importStrategy;
        CacheFileKey = cacheFileKey;
    }

    /// <summary>
    ///     Returns the import strategy to use
    /// </summary>
    public ImportStrategy ImportStrategy { get; }

    /// <summary>
    ///     Returns the cache file key
    /// </summary>
    public string CacheFileKey { get; }
}