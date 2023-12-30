namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
/// Arguments for import runtime data
/// </summary>
public record ImportRtCommandRequest: CommandBaseRequest
{
    /// <summary>
    /// Returns the cache file key
    /// </summary>
    public string CacheFileKey { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="cacheFileKey"></param>
    public ImportRtCommandRequest(string tenantId, string cacheFileKey) 
        : base(tenantId)
    {
        CacheFileKey = cacheFileKey;
    }
}