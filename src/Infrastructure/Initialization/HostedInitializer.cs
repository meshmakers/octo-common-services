using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.Infrastructure.Initialization;

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

            foreach (var asyncInitializationService in services)
            {
                try
                {
                    await asyncInitializationService.InitializeAsync();
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
