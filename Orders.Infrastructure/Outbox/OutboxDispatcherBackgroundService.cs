using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orders.Infrastructure.Outbox;

public sealed class OutboxDispatcherBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcherBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox dispatcher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<OutboxDispatcher>();
                var dispatched = await dispatcher.DispatchNextAsync(stoppingToken);

                if (!dispatched)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected failure in outbox dispatcher.");
                await Task.Delay(FailureDelay, stoppingToken);
            }
        }

        logger.LogInformation("Outbox dispatcher stopped.");
    }
}
