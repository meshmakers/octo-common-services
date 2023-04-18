using System.Threading.Tasks;

namespace Meshmakers.Octo.Backend.Infrastructure.Initialization;

public interface IAsyncInitializationService
{
    Task InitializeAsync();
}
