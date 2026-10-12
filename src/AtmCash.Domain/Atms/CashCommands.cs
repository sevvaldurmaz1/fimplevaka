using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Atms;

public sealed record CassetteConfiguration(int Number, Currency Currency, int Denomination);
public sealed record CashLoad(int CassetteNumber, Currency Currency, int Denomination, int Count);
