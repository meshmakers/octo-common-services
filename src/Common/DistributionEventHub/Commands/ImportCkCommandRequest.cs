namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
/// Arguments for import construction kit data
/// </summary>
public record ImportCkCommandRequest: CommandBaseRequest
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
    public ImportCkCommandRequest(string tenantId, string cacheFileKey) 
        : base(tenantId)
    {
        CacheFileKey = cacheFileKey;
    }
}