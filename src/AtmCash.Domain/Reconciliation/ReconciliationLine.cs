using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Reconciliation;

public sealed class ReconciliationLine
{
    private ReconciliationLine() { }

    internal ReconciliationLine(Guid reconciliationId, Guid cassetteId, int cassetteNumber, Currency currency,
        int denomination, int systemCount, int countedCount, decimal tryRate)
    {
        Id = Guid.NewGuid();
        ReconciliationId = reconciliationId;
        CassetteId = cassetteId;
        CassetteNumber = cassetteNumber;
        Currency = currency;
        Denomination = denomination;
        SystemCount = systemCount;
        CountedCount = countedCount;
        TryRate = tryRate;
    }

    public Guid Id { get; private set; }
    public Guid ReconciliationId { get; private set; }
    public Guid CassetteId { get; private set; }
    public int CassetteNumber { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public int SystemCount { get; private set; }
    public int CountedCount { get; private set; }
    public decimal TryRate { get; private set; }
    public int Difference => CountedCount - SystemCount;
    public decimal AbsoluteDifferenceTry => Math.Abs(Difference * Denomination * TryRate);
}
