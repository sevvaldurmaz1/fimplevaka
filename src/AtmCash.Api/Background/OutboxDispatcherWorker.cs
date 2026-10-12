using AtmCash.Application.Alerts;

namespace AtmCash.Api.Background;

public sealed class OutboxDispatcherWorker(IServiceScopeFactory scopes, ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Outbox dispatch failed; unsent events will be retried"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
