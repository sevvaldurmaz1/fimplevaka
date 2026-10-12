namespace AtmCash.Application.Operations;

public sealed class ReconciliationNotFoundException(Guid id) : Exception($"Reconciliation {id} was not found at this ATM.");
