namespace Meshmakers.Octo.Services.Common.Cors;

/// <summary>
///     Gets the known origin from the database.
/// </summary>
public interface IKnownOriginsProvider
{
    /// <summary>
    ///     Gets the known origins from the database.
    /// </summary>
    /// <returns></returns>
    Task<IReadOnlyCollection<string>> GetKnownOriginsAsync();
}