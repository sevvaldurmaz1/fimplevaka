using AtmCash.Application.Withdrawals;

namespace AtmCash.Api.Background;

public sealed class ReservationExpirationWorker(IServiceScopeFactory scopes,
    ILogger<ReservationExpirationWorker> logger) : BackgroundService
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
                    var withdrawals = scope.ServiceProvider.GetRequiredService<WithdrawalService>();
                    var count = await withdrawals.ExpireDueAsync(stoppingToken);
                    if (count > 0) logger.LogInformation("Released {Count} expired withdrawal quotes", count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to release expired withdrawal quotes; the next scan will retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
