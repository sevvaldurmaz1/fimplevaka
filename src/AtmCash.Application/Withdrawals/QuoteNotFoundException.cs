namespace AtmCash.Application.Withdrawals;

public sealed class QuoteNotFoundException(Guid id) : Exception($"Withdrawal quote {id} was not found at this ATM.");
