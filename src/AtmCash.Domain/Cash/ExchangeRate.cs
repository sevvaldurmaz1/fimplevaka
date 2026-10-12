using AtmCash.Domain.Common;

namespace AtmCash.Domain.Cash;

public sealed class ExchangeRate
{
    private ExchangeRate() { }
    public ExchangeRate(Currency currency, decimal tryRate)
    {
        if (!Enum.IsDefined(currency) || tryRate <= 0 || (currency == Currency.TRY && tryRate != 1))
            throw new DomainException("invalid_exchange_rate", "Rates must be positive; the TRY rate must be one.");
        Currency = currency;
        TryRate = tryRate;
    }
    public Currency Currency { get; private set; }
    public decimal TryRate { get; private set; }
    public static decimal Require(IReadOnlyDictionary<Currency, decimal> rates, Currency currency)
        => rates.TryGetValue(currency, out var rate) && rate > 0 ? rate
            : throw new DomainException("exchange_rate_unavailable", "A required TRY conversion rate is unavailable.");
}
