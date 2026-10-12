using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Application.Withdrawals;

public sealed record QuoteOptionView(QuoteOptionKind Kind, int Amount,
    IReadOnlyDictionary<int, int> Notes, IReadOnlyList<CassetteAllocation> Cassettes);
public sealed record QuoteView(Guid Id, Guid AtmId, Currency Currency, int RequestedAmount, QuoteStatus Status,
    DateTimeOffset ExpiresAt, int ReservedAmount, IReadOnlyList<QuoteOptionView> Options);
public sealed record ConfirmationView(Guid QuoteId, Guid AtmId, Currency Currency, int Amount,
    DateTimeOffset ConfirmedAt, IReadOnlyDictionary<int, int> Notes, IReadOnlyList<CassetteAllocation> Cassettes);

public static class WithdrawalMapping
{
    public static QuoteView ToView(this WithdrawalQuote quote) => new(quote.Id, quote.AtmId, quote.Currency,
        quote.RequestedAmount, quote.Status, quote.ExpiresAt, quote.Status == QuoteStatus.ACTIVE ? quote.ReservedAmount : 0,
        quote.Options.OrderBy(item => item.Amount).Select(option => new QuoteOptionView(option.Kind, option.Amount,
            Breakdown(option), option.GetNotes().OrderBy(item => item.CassetteId).ToArray())).ToArray());

    public static ConfirmationView ToConfirmationView(this WithdrawalQuote quote)
    {
        if (quote.Status != QuoteStatus.CONFIRMED || quote.ConfirmedAt is null || quote.ConfirmedAmount is null)
            throw new InvalidOperationException("A confirmation result requires a confirmed quote.");
        var selected = quote.Options.Single(item => item.Amount == quote.ConfirmedAmount);
        return new(quote.Id, quote.AtmId, quote.Currency, selected.Amount, quote.ConfirmedAt.Value,
            Breakdown(selected), selected.GetNotes().OrderBy(item => item.CassetteId).ToArray());
    }

    private static IReadOnlyDictionary<int, int> Breakdown(QuoteOption option)
        => option.Allocations.GroupBy(item => item.Denomination).OrderByDescending(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
}
