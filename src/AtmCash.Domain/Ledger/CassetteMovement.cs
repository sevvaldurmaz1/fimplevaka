using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Ledger;

public enum MovementType
{
    LOAD, UNLOAD, WITHDRAWAL, DEPOSIT, RESERVATION, RESERVATION_RELEASE, RECONCILIATION_ADJUSTMENT
}

public sealed class CassetteMovement
{
    private CassetteMovement() { }

    internal CassetteMovement(Guid cassetteId, Currency currency, int denomination,
        MovementType type, int noteDelta, int reservedDelta, DateTimeOffset occurredAt, Guid? referenceId = null)
    {
        Id = Guid.NewGuid();
        CassetteId = cassetteId;
        Currency = currency;
        Denomination = denomination;
        Type = type;
        NoteDelta = noteDelta;
        ReservedDelta = reservedDelta;
        OccurredAt = occurredAt;
        ReferenceId = referenceId;
    }

    public Guid Id { get; private set; }
    public Guid CassetteId { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public MovementType Type { get; private set; }
    public int NoteDelta { get; private set; }
    public int ReservedDelta { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? ReferenceId { get; private set; }
}
