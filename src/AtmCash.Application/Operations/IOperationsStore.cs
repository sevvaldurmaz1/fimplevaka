using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliation;

namespace AtmCash.Application.Operations;

public sealed record OperationsSnapshot(IReadOnlyList<Atm> Atms, IReadOnlyDictionary<Currency, decimal> Rates);
public sealed record LedgerBalance(Guid CassetteId, Currency Currency, int Denomination, long NoteCount, long ReservedCount);
public sealed record ConsistencySnapshot(Atm Atm, IReadOnlyList<LedgerBalance> Balances);

public interface IOperationsStore
{
    Task<T> ExecuteAsync<T>(Guid atmId, Func<IOperationsSession, CancellationToken, Task<T>> operation, CancellationToken token);
    Task<CashReconciliation?> FindReconciliationAsync(Guid atmId, Guid id, CancellationToken token);
    Task<OperationsSnapshot> ReadReportAsync(Guid? atmId, CancellationToken token);
    Task<ConsistencySnapshot> ReadConsistencyAsync(Guid atmId, CancellationToken token);
}

public interface IOperationsSession
{
    Atm Atm { get; }
    Task<IReadOnlyDictionary<Currency, decimal>> ReadRatesAsync(CancellationToken token);
    Task<CashReconciliation?> FindReconciliationAsync(Guid id, CancellationToken token);
    void AddReconciliation(CashReconciliation reconciliation);
}
