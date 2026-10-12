using AtmCash.Domain.Cash;
using AtmCash.Domain.Deposits;

namespace AtmCash.Application.Deposits;

public sealed record DepositView(Guid Id, Guid AtmId, DateTimeOffset ProcessedAt,
    IReadOnlyList<DepositPlacement> Accepted, IReadOnlyList<ReturnedDepositNotes> Returned,
    int UnrecognizedReturnedCount, int AcceptedNoteCount, long ReturnedRecognizedNoteCount,
    IReadOnlyDictionary<Currency, int> AcceptedTotals);

public static class DepositMapping
{
    public static DepositView ToView(this DepositResult result) => new(result.Id, result.AtmId, result.ProcessedAt,
        result.Accepted, result.Returned, result.UnrecognizedReturnedCount,
        result.Accepted.Sum(note => note.Count), result.Returned.Sum(note => note.Count),
        result.Accepted.GroupBy(note => note.Currency).OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Sum(note => note.Denomination * note.Count)));
}
