namespace AtmCash.Domain.Cash;

public sealed record NoteBatch(Currency Currency, int Denomination, int Count);
