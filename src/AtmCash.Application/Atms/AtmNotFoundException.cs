namespace AtmCash.Application.Atms;

public sealed class AtmNotFoundException(Guid id) : Exception($"ATM {id} was not found.");
