using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Observability;

public class StartupBackgroundService(StartupHealthCheck healthCheck) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Simulate the effect of a long-running task.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        healthCheck.StartupCompleted = true;
    }
}