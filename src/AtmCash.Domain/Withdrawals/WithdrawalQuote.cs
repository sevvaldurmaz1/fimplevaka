using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Withdrawals;

public enum QuoteStatus { ACTIVE, CONFIRMED, CANCELLED, EXPIRED }
public enum QuoteOptionKind { Exact, Lower, Upper }
public sealed record QuoteOffer(QuoteOptionKind Kind, int Amount, IReadOnlyCollection<CassetteAllocation> Notes);

public sealed class WithdrawalQuote
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly List<QuoteOption> _options = [];
    private WithdrawalQuote() { }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public string CardHash { get; private set; } = string.Empty;
    public Currency Currency { get; private set; }
    public int RequestedAmount { get; private set; }
    public int ReservedAmount { get; private set; }
    public QuoteStatus Status { get; private set; } = QuoteStatus.ACTIVE;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int? ConfirmedAmount { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateOnly? ConfirmedBusinessDate { get; private set; }
    public IReadOnlyCollection<QuoteOption> Options => _options.AsReadOnly();

    public static WithdrawalQuote Create(Guid atmId, string cardHash, Currency currency,
        int requestedAmount, DateTimeOffset now, IReadOnlyCollection<QuoteOffer> offers)
    {
        if (atmId == Guid.Empty || cardHash.Length != 64 || !Enum.IsDefined(currency) || requestedAmount <= 0)
            throw new DomainException("invalid_quote", "A quote requires a valid ATM, card fingerprint, currency and amount.");
        if (offers.Count is < 1 or > 2 || offers.Select(item => item.Kind).Distinct().Count() != offers.Count
            || offers.Any(item => !Enum.IsDefined(item.Kind) || item.Amount <= 0 || item.Notes.Count == 0
                || item.Notes.Any(note => note.CassetteId == Guid.Empty || note.Count is < 1 or > 100 || !Banknotes.IsSupported(note.Denomination))
                || item.Notes.Select(note => note.CassetteId).Distinct().Count() != item.Notes.Count
                || item.Notes.Sum(note => (long)note.Count * note.Denomination) != item.Amount
                || (item.Kind == QuoteOptionKind.Exact && (item.Amount != requestedAmount || offers.Count != 1))
                || (item.Kind == QuoteOptionKind.Lower && item.Amount >= requestedAmount)
                || (item.Kind == QuoteOptionKind.Upper && item.Amount <= requestedAmount)))
            throw new DomainException("invalid_quote_options", "Quote options must contain valid exact or lower/upper note allocations.");
        if (offers.SelectMany(item => item.Notes).GroupBy(item => item.CassetteId)
            .Any(group => group.Select(item => item.Denomination).Distinct().Count() != 1))
            throw new DomainException("invalid_quote_options", "A cassette cannot contain different denominations across quote options.");
        var quote = new WithdrawalQuote
        {
            Id = Guid.NewGuid(), AtmId = atmId, CardHash = cardHash, Currency = currency,
            RequestedAmount = requestedAmount, ReservedAmount = offers.Max(item => item.Amount),
            CreatedAt = now, ExpiresAt = now + Lifetime
        };
        foreach (var offer in offers)
            quote._options.Add(new QuoteOption(quote.Id, offer));
        return quote;
    }

    public IReadOnlyList<CassetteAllocation> GetReservedNotes()
        => _options.SelectMany(option => option.Allocations).GroupBy(item => item.CassetteId)
            .Select(group => new CassetteAllocation(group.Key, group.First().Denomination, group.Max(item => item.Count))).ToArray();

    public QuoteOption ValidateConfirmation(int amount, DateTimeOffset now)
    {
        if (Status == QuoteStatus.EXPIRED || (Status == QuoteStatus.ACTIVE && now >= ExpiresAt))
            throw new DomainException("quote_expired", "The quote expired. Request a new quote.");
        if (Status == QuoteStatus.CANCELLED)
            throw new DomainException("quote_cancelled", "A cancelled quote cannot be confirmed.");
        if (Status == QuoteStatus.CONFIRMED)
            throw new DomainException("quote_already_confirmed", "The quote has already been confirmed. Replay the original idempotency key.");
        return _options.SingleOrDefault(item => item.Amount == amount)
            ?? throw new DomainException("amount_not_offered", "Confirm one of the amounts offered in the quote.");
    }

    public void Confirm(int amount, DateTimeOffset now, DateOnly businessDate)
    {
        ValidateConfirmation(amount, now);
        Status = QuoteStatus.CONFIRMED;
        ConfirmedAmount = amount;
        ConfirmedAt = now;
        ConfirmedBusinessDate = businessDate;
    }

    public bool Cancel()
    {
        if (Status == QuoteStatus.CONFIRMED)
            throw new DomainException("quote_already_confirmed", "A confirmed quote cannot be cancelled.");
        if (Status != QuoteStatus.ACTIVE) return false;
        Status = QuoteStatus.CANCELLED;
        return true;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (Status != QuoteStatus.ACTIVE || now < ExpiresAt) return false;
        Status = QuoteStatus.EXPIRED;
        return true;
    }
}
