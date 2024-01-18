using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

internal class HostedInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;

    public HostedInitializer(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();

        //a scope is required to be able to request scoped services.
        using (var scope = _serviceProvider.CreateScope())
        {
            var services = scope.ServiceProvider.GetServices<IAsyncInitializationService>();

            foreach (var asyncInitializationService in services.OrderBy(s => s.Order))
            {
                try
                {
                    await asyncInitializationService.InitializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Any())
            {
                throw new AggregateException(errors);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}