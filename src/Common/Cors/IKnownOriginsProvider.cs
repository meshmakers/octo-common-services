namespace Meshmakers.Octo.Services.Common.Cors;

/// <summary>
///     Gets the known origin from the database.
/// </summary>
public interface IKnownOriginsProvider
{
    /// <summary>
    ///     Gets the known origins from the database.
    /// </summary>
    /// <param name="tenantId">Id of tenant</param>
    /// <returns>List of allowed origins of the tenant</returns>
    Task<IReadOnlyCollection<string>> GetKnownOriginsAsync(string tenantId);
}