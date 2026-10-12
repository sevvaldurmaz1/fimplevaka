using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Application.Withdrawals;

public sealed record QuoteIdentity(string CardHash);
public sealed record WithdrawalLocks(Guid AtmId, string? CardHash = null, string? IdempotencyKey = null);

public interface IWithdrawalStore
{
    Task<QuoteIdentity?> FindIdentityAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FindAtmsWithExpiredQuotesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<T> ExecuteAsync<T>(WithdrawalLocks locks,
        Func<IWithdrawalSession, CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>Queries and writes that share the locks, transaction and ATM snapshot.</summary>
public interface IWithdrawalSession
{
    Atm Atm { get; }
    Task<WithdrawalQuote?> FindQuoteAsync(Guid quoteId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WithdrawalQuote>> FindExpiredQuotesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> GetDailyUsageAsync(string cardHash, Currency currency, DateOnly day, DateTimeOffset now, CancellationToken cancellationToken);
    Task<WithdrawalConfirmation?> FindConfirmationAsync(string key, CancellationToken cancellationToken);
    void AddQuote(WithdrawalQuote quote);
    void AddConfirmation(WithdrawalConfirmation confirmation);
}
