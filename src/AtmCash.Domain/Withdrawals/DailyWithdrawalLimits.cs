using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Withdrawals;

public static class DailyWithdrawalLimits
{
    public static int For(Currency currency) => currency switch
    {
        Currency.TRY => 20_000,
        Currency.USD or Currency.EUR => 1_000,
        _ => throw new DomainException("unsupported_currency", "Only TRY, USD and EUR are supported.")
    };
}
