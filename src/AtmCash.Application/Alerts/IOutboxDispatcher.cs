namespace AtmCash.Application.Alerts;

public interface IOutboxDispatcher
{
    Task<int> DispatchAsync(CancellationToken cancellationToken);
}
