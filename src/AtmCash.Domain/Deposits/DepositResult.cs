using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Deposits;

public sealed record DepositPlacement(Guid CassetteId, int CassetteNumber, Currency Currency, int Denomination, int Count);
public sealed record ReturnedDepositNotes(Currency Currency, int Denomination, long Count);

public sealed record DepositResult(Guid Id, Guid AtmId, DateTimeOffset ProcessedAt,
    IReadOnlyList<DepositPlacement> Accepted, IReadOnlyList<ReturnedDepositNotes> Returned, int UnrecognizedReturnedCount)
{
    public const int MaximumNoteGroups = 60;
}
