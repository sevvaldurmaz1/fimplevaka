using AtmCash.Domain.Common;

namespace AtmCash.Domain.Cash;

public static class Banknotes
{
    public static bool IsSupported(int denomination) => denomination is 20 or 50 or 100 or 500;

    public static void Validate(Currency currency, int denomination)
    {
        if (!Enum.IsDefined(currency))
            throw new DomainException("unsupported_currency", "Only TRY, USD and EUR are supported.");
        if (!IsSupported(denomination))
            throw new DomainException("unsupported_denomination", "Only 20, 50, 100 and 500 banknotes are supported.");
    }
}
