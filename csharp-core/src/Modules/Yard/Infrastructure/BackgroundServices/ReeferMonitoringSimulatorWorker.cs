using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Infrastructure.BackgroundServices;

public class ReeferMonitoringSimulatorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReeferMonitoringSimulatorWorker> _logger;

    public ReeferMonitoringSimulatorWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReeferMonitoringSimulatorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool isEnabled = _configuration.GetValue<bool>("ReeferSimulator:Enabled", true);
        int intervalSeconds = _configuration.GetValue<int>("ReeferSimulator:IntervalSeconds", 6);

        if (!isEnabled)
        {
            _logger.LogInformation("ReeferMonitoringSimulatorWorker is disabled by configuration.");
            return;
        }

        _logger.LogInformation("ReeferMonitoringSimulatorWorker started. Cycle interval: {Interval} seconds.", intervalSeconds);

        // Delay slightly on startup to let host initialize
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var monitoringService = scope.ServiceProvider.GetRequiredService<IReeferMonitoringService>();

                await monitoringService.RunSimulationCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during Reefer monitoring simulation cycle.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ReeferMonitoringSimulatorWorker has stopped.");
    }
}
