using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

internal class HostedInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private FailedTenantRetryLoop? _retryLoop;

    public HostedInitializer(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();

        // AB#7271: the retry of failed tenant setups must run while the startup pass below is still busy with
        // other tenants. Hosted services start sequentially, so a separate BackgroundService would only start
        // after this method returned. Only present when the service uses AddOctoServiceInfrastructure.
        _retryLoop = _serviceProvider.GetService<FailedTenantRetryLoop>();
        _retryLoop?.Start();

        try
        {
            //a scope is required to be able to request scoped services.
            using var scope = _serviceProvider.CreateScope();
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
        catch
        {
            // A failed start (e.g. the system tenant, fail-fast by design) gets no StopAsync call from the host.
            await StopRetryLoopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return StopRetryLoopAsync();
    }

    private async Task StopRetryLoopAsync()
    {
        var loop = Interlocked.Exchange(ref _retryLoop, null);
        if (loop is not null)
        {
            await loop.StopAsync().ConfigureAwait(false);
        }
    }
}
