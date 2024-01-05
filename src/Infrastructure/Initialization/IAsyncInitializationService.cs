namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Interface for an initialization service, executed during startup of the application
/// </summary>
public interface IAsyncInitializationService
{
    /// <summary>
    ///     The order the service should be executed in. Lower numbers are executed first.
    /// </summary>
    public int Order { get; }

    /// <summary>
    ///     Initialize the service
    /// </summary>
    /// <returns></returns>
    Task InitializeAsync();
}