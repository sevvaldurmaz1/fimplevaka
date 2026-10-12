using AtmCash.Domain.Cash;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Application.Withdrawals;

public sealed class WithdrawalService(IWithdrawalStore store, ICardFingerprint cards,
    NoteSelector selector, CassetteAllocator allocator, TimeProvider clock)
{
    private static readonly TimeZoneInfo BusinessTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private sealed record ConfirmationAttempt(ConfirmationView? Result, DomainException? Error);

    public async Task<QuoteView> QuoteAsync(Guid atmId, string cardNumber, Currency currency, int amount,
        CancellationToken cancellationToken)
    {
        var cardHash = cards.Compute(cardNumber);
        var dailyLimit = DailyWithdrawalLimits.For(currency);
        if (amount <= 0)
            throw new DomainException("invalid_withdrawal_amount", "Withdrawal amount must be positive.");
        return await store.ExecuteAsync(new WithdrawalLocks(atmId, cardHash), async (session, token) =>
        {
            var now = OperationTime();
            session.Atm.EnsureInService();
            await ReleaseExpiredAsync(session, now, token);
            var used = await session.GetDailyUsageAsync(cardHash, currency, BusinessDate(now), now, token);
            var remaining = dailyLimit - used;
            if (amount > remaining)
                throw new DomainException("daily_limit_exceeded", "The request exceeds the remaining daily limit, including active quotes.");
            var stock = session.Atm.Cassettes.Where(item => item.Currency == currency
                    && item.Type == CassetteType.Recycling)
                .Select(item => new NoteStock(item.Denomination!.Value, item.Inventory.Sum(note => note.AvailableCount))).ToArray();
            var selection = selector.Select(stock, amount, remaining);
            var offers = new List<QuoteOffer>();
            AddOffer(QuoteOptionKind.Exact, selection.Exact);
            AddOffer(QuoteOptionKind.Lower, selection.Lower);
            AddOffer(QuoteOptionKind.Upper, selection.Upper);
            if (offers.Count == 0)
                throw new DomainException("cash_unavailable", "No positive amount can be paid with the available notes and remaining limit.");
            var quote = WithdrawalQuote.Create(atmId, cardHash, currency, amount, now, offers);
            session.Atm.ReserveNotes(currency, quote.GetReservedNotes(), now, quote.Id);
            session.AddQuote(quote);
            return quote.ToView();

            void AddOffer(QuoteOptionKind kind, NoteBreakdown? breakdown)
            {
                if (breakdown is not null)
                    offers.Add(new QuoteOffer(kind, breakdown.Amount, allocator.Allocate(session.Atm, currency, breakdown)));
            }
        }, cancellationToken);
    }

    public async Task<ConfirmationView> ConfirmAsync(Guid atmId, Guid quoteId, int amount, string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new DomainException("invalid_idempotency_key", "Provide an Idempotency-Key of 1–128 characters.");
        var identity = await FindIdentityAsync(atmId, quoteId, cancellationToken);
        var attempt = await store.ExecuteAsync(new WithdrawalLocks(atmId, identity.CardHash, key), async (session, token) =>
        {
            var previous = await session.FindConfirmationAsync(key, token);
            if (previous is not null && (previous.QuoteId != quoteId || previous.Amount != amount))
                throw new DomainException("idempotency_conflict", "This key was already used for a different confirm request.");
            var quote = await session.FindQuoteAsync(quoteId, token) ?? throw new QuoteNotFoundException(quoteId);
            if (previous is not null)
                return new ConfirmationAttempt(quote.ToConfirmationView(), null);

            var now = OperationTime();
            await ReleaseExpiredAsync(session, now, token);
            QuoteOption selected;
            try { selected = quote.ValidateConfirmation(amount, now); }
            catch (DomainException error) { return new ConfirmationAttempt(null, error); }
            var day = BusinessDate(now);
            var used = await session.GetDailyUsageAsync(identity.CardHash, quote.Currency, day, now, token);
            if (used > DailyWithdrawalLimits.For(quote.Currency))
                return new ConfirmationAttempt(null, new DomainException("daily_limit_exceeded", "The daily withdrawal limit is exceeded."));
            session.Atm.ConfirmWithdrawal(quote.Currency, quote.GetReservedNotes(), selected.GetNotes(), now, quote.Id);
            quote.Confirm(amount, now, day);
            session.AddConfirmation(new WithdrawalConfirmation(key, quoteId, amount));
            return new ConfirmationAttempt(quote.ToConfirmationView(), null);
        }, cancellationToken);
        // An expired confirm must commit its reservation release before returning the business error.
        if (attempt.Error is not null) throw attempt.Error;
        return attempt.Result!;
    }

    public async Task<QuoteView> GetAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
    {
        await FindIdentityAsync(atmId, quoteId, cancellationToken);
        return await store.ExecuteAsync(new WithdrawalLocks(atmId), async (session, token) =>
        {
            await ReleaseExpiredAsync(session, OperationTime(), token);
            return (await session.FindQuoteAsync(quoteId, token) ?? throw new QuoteNotFoundException(quoteId)).ToView();
        }, cancellationToken);
    }

    public async Task<QuoteView> CancelAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
    {
        await FindIdentityAsync(atmId, quoteId, cancellationToken);
        return await store.ExecuteAsync(new WithdrawalLocks(atmId), async (session, token) =>
        {
            var now = OperationTime();
            await ReleaseExpiredAsync(session, now, token);
            var quote = await session.FindQuoteAsync(quoteId, token) ?? throw new QuoteNotFoundException(quoteId);
            if (quote.Cancel())
                session.Atm.ReleaseNotes(quote.Currency, quote.GetReservedNotes(), now, quote.Id);
            return quote.ToView();
        }, cancellationToken);
    }

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var atmIds = await store.FindAtmsWithExpiredQuotesAsync(OperationTime(), cancellationToken);
        var released = 0;
        foreach (var atmId in atmIds)
            released += await store.ExecuteAsync(new WithdrawalLocks(atmId),
                (session, token) => ReleaseExpiredAsync(session, OperationTime(), token), cancellationToken);
        return released;
    }

    private async Task<QuoteIdentity> FindIdentityAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
        => await store.FindIdentityAsync(atmId, quoteId, cancellationToken) ?? throw new QuoteNotFoundException(quoteId);

    private static DateOnly BusinessDate(DateTimeOffset now)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, BusinessTimeZone).DateTime);

    // Canonical precision keeps the first result identical to a persisted/replayed result.
    private DateTimeOffset OperationTime()
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static async Task<int> ReleaseExpiredAsync(IWithdrawalSession session, DateTimeOffset now, CancellationToken token)
    {
        var quotes = await session.FindExpiredQuotesAsync(now, token);
        foreach (var quote in quotes)
        {
            if (quote.Expire(now))
                session.Atm.ReleaseNotes(quote.Currency, quote.GetReservedNotes(), now, quote.Id);
        }
        return quotes.Count;
    }
}
