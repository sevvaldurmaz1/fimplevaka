using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class PostgresWithdrawalStore(CashDbContext db) : IWithdrawalStore
{
    public Task<QuoteIdentity?> FindIdentityAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
        => db.Quotes.AsNoTracking().Where(quote => quote.Id == quoteId && quote.AtmId == atmId)
            .Select(quote => new QuoteIdentity(quote.CardHash)).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> FindAtmsWithExpiredQuotesAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => await db.Quotes.AsNoTracking().Where(quote => quote.Status == QuoteStatus.ACTIVE && quote.ExpiresAt <= now)
            .Select(quote => quote.AtmId).Distinct().OrderBy(id => id).Take(100).ToArrayAsync(cancellationToken);

    public async Task<T> ExecuteAsync<T>(WithdrawalLocks locks,
        Func<IWithdrawalSession, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // All transactions that increase daily usage use this order: key -> card -> ATM.
        // Cancellation and expiration only decrease usage and acquire the ATM lock alone.
        if (locks.IdempotencyKey is not null)
            await AdvisoryLockAsync("confirm:" + locks.IdempotencyKey, cancellationToken);
        if (locks.CardHash is not null)
            await AdvisoryLockAsync("card:" + locks.CardHash, cancellationToken);
        var atm = await AtmLock.LoadAsync(db, locks.AtmId, cancellationToken);
        var result = await operation(new Session(db, atm), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private Task AdvisoryLockAsync(string identity, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = BinaryPrimitives.ReadInt64BigEndian(hash);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    private sealed class Session(CashDbContext db, Atm atm) : IWithdrawalSession
    {
        public Atm Atm { get; } = atm;

        private IQueryable<WithdrawalQuote> Quotes
            => db.Quotes.AsSingleQuery().Where(quote => quote.AtmId == Atm.Id)
                .Include(quote => quote.Options).ThenInclude(option => option.Allocations);

        public Task<WithdrawalQuote?> FindQuoteAsync(Guid quoteId, CancellationToken cancellationToken)
            => Quotes.SingleOrDefaultAsync(quote => quote.Id == quoteId, cancellationToken);

        public async Task<IReadOnlyList<WithdrawalQuote>> FindExpiredQuotesAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => await Quotes.Where(quote => quote.Status == QuoteStatus.ACTIVE && quote.ExpiresAt <= now).ToArrayAsync(cancellationToken);

        public Task<int> GetDailyUsageAsync(string cardHash, Currency currency, DateOnly day, DateTimeOffset now,
            CancellationToken cancellationToken)
            => db.Quotes.Where(quote => quote.CardHash == cardHash && quote.Currency == currency
                    && ((quote.Status == QuoteStatus.CONFIRMED && quote.ConfirmedBusinessDate == day)
                        || (quote.Status == QuoteStatus.ACTIVE && quote.ExpiresAt > now)))
                .Select(quote => quote.Status == QuoteStatus.CONFIRMED ? quote.ConfirmedAmount!.Value : quote.ReservedAmount)
                .SumAsync(cancellationToken);

        public Task<WithdrawalConfirmation?> FindConfirmationAsync(string key, CancellationToken cancellationToken)
            => db.Confirmations.AsNoTracking().SingleOrDefaultAsync(item => item.Key == key, cancellationToken);

        public void AddQuote(WithdrawalQuote quote) => db.Quotes.Add(quote);
        public void AddConfirmation(WithdrawalConfirmation confirmation) => db.Confirmations.Add(confirmation);
    }
}
