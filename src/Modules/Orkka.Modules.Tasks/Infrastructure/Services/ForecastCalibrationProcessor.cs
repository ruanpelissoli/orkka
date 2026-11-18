namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Worker loop that scores forecasts once their tasks finish. Cheap when idle: the pending set
/// is empty unless a forecast was shown and its task has since moved on.
/// </summary>
public sealed class ForecastCalibrationProcessor(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ForecastCalibrationProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            configuration.GetValue<int?>("Worker:ForecastCalibrationIntervalMinutes") ?? 15);
        var batchSize = configuration.GetValue<int?>("Worker:ForecastCalibrationBatchSize") ?? 200;

        logger.LogInformation(
            "ForecastCalibrationProcessor started with {Interval}m interval", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ForecastCalibrationService>();
                await service.ScorePendingAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Forecast calibration sweep failed — retrying next interval");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
