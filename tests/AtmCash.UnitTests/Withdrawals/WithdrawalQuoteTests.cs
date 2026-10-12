using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.UnitTests.Withdrawals;

public sealed class WithdrawalQuoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static WithdrawalQuote ExactQuote() => WithdrawalQuote.Create(Guid.NewGuid(), new string('a', 64),
        Currency.TRY, 100, Now, [new(QuoteOptionKind.Exact, 100, [new(Guid.NewGuid(), 100, 1)])]);

    [Fact]
    public void AlternativesReserveMaximumNotesPerCassetteAndMaximumOfferedAmount()
    {
        var fifties = Guid.NewGuid();
        var twenties = Guid.NewGuid();
        var quote = WithdrawalQuote.Create(Guid.NewGuid(), new string('a', 64), Currency.TRY, 80, Now,
        [
            new(QuoteOptionKind.Lower, 70, [new(fifties, 50, 1), new(twenties, 20, 1)]),
            new(QuoteOptionKind.Upper, 90, [new(fifties, 50, 1), new(twenties, 20, 2)])
        ]);
        Assert.Equal(90, quote.ReservedAmount);
        Assert.Equal(1, quote.GetReservedNotes().Single(item => item.CassetteId == fifties).Count);
        Assert.Equal(2, quote.GetReservedNotes().Single(item => item.CassetteId == twenties).Count);
        Assert.Equal(Now.AddSeconds(60), quote.ExpiresAt);
    }

    [Fact]
    public void ConfirmationRecordsActualBusinessDateAndSelectedAmount()
    {
        var quote = ExactQuote();
        var day = new DateOnly(2026, 10, 13);
        quote.Confirm(100, Now.AddSeconds(30), day);
        Assert.Equal(QuoteStatus.CONFIRMED, quote.Status);
        Assert.Equal(100, quote.ConfirmedAmount);
        Assert.Equal(day, quote.ConfirmedBusinessDate);
        Assert.Throws<DomainException>(() => quote.Cancel());
    }

    [Fact]
    public void ConfirmationAtExpiryBoundaryIsRejected()
    {
        var quote = ExactQuote();
        Assert.NotNull(quote.ValidateConfirmation(100, Now.AddSeconds(59.999)));
        Assert.Equal("quote_expired", Assert.Throws<DomainException>(() =>
            quote.ValidateConfirmation(100, Now.AddSeconds(60))).Code);
        Assert.True(quote.Expire(Now.AddSeconds(60)));
        Assert.False(quote.Expire(Now.AddSeconds(61)));
    }

    [Fact]
    public void RepeatedCancellationDoesNotReleaseReservationTwice()
    {
        var quote = ExactQuote();
        Assert.True(quote.Cancel());
        Assert.False(quote.Cancel());
        Assert.Equal("quote_cancelled", Assert.Throws<DomainException>(() => quote.ValidateConfirmation(100, Now)).Code);
    }

    [Fact]
    public void AmountNotOfferedCannotBeConfirmed()
        => Assert.Equal("amount_not_offered", Assert.Throws<DomainException>(() => ExactQuote().ValidateConfirmation(120, Now)).Code);

    [Theory]
    [InlineData(Currency.TRY, 20000)]
    [InlineData(Currency.USD, 1000)]
    [InlineData(Currency.EUR, 1000)]
    public void DailyLimitsAreCurrencySpecific(Currency currency, int limit)
        => Assert.Equal(limit, DailyWithdrawalLimits.For(currency));
}
