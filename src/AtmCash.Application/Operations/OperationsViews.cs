using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliation;

namespace AtmCash.Application.Operations;

public sealed record ReconciliationLineView(int CassetteNumber, Currency Currency, int Denomination,
    int SystemCount, int CountedCount, int Difference, decimal TryRate, decimal AbsoluteDifferenceTry);
public sealed record ReconciliationView(Guid Id, Guid AtmId, ReconciliationStatus Status, string SubmittedBy,
    string? ApprovedBy, DateTimeOffset SubmittedAt, DateTimeOffset? AppliedAt, decimal TotalDifferenceTry,
    IReadOnlyList<ReconciliationLineView> Lines);
public sealed record CurrencyBalance(Currency Currency, long NoteCount, long ReservedCount, long AvailableCount,
    long Balance, long ReservedBalance, long AvailableBalance, decimal TryRate);
public sealed record CassetteReport(Guid Id, int Number, CassetteType Type, Currency? Currency, int? Denomination,
    int Capacity, int NoteCount, int ReservedCount, int AvailableCount, IReadOnlyList<InventoryView> Inventory);
public sealed record AtmReport(Guid Id, string Name, AtmState State, Guid? PendingReconciliationId,
    IReadOnlyList<CassetteReport> Cassettes, IReadOnlyList<CurrencyBalance> Totals,
    decimal TotalInTry, decimal ReservedTotalInTry, decimal AvailableTotalInTry);
public sealed record OperationsReport(IReadOnlyList<AtmReport> Atms, IReadOnlyList<CurrencyBalance> Totals,
    decimal TotalInTry, decimal ReservedTotalInTry, decimal AvailableTotalInTry);
public sealed record BalanceDifference(Guid CassetteId, int CassetteNumber, Currency Currency, int Denomination,
    long StoredNoteCount, long CalculatedNoteCount, long StoredReservedCount, long CalculatedReservedCount);
public sealed record ConsistencyView(Guid AtmId, bool IsConsistent, IReadOnlyList<BalanceDifference> Differences);

public static class ReconciliationMapping
{
    public static ReconciliationView ToView(this CashReconciliation reconciliation) => new(
        reconciliation.Id, reconciliation.AtmId, reconciliation.Status, reconciliation.SubmittedBy,
        reconciliation.ApprovedBy, reconciliation.SubmittedAt, reconciliation.AppliedAt, reconciliation.TotalDifferenceTry,
        reconciliation.Lines.OrderBy(line => line.CassetteNumber).ThenBy(line => line.Currency).ThenBy(line => line.Denomination)
            .Select(line => new ReconciliationLineView(line.CassetteNumber, line.Currency, line.Denomination,
                line.SystemCount, line.CountedCount, line.Difference, line.TryRate, line.AbsoluteDifferenceTry)).ToArray());
}
