using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Application.Withdrawals;

public sealed record NoteStock(int Denomination, int Count);
public sealed record NoteBreakdown(int Amount, IReadOnlyDictionary<int, int> Notes);
public sealed record WithdrawalSelection(NoteBreakdown? Exact, NoteBreakdown? Lower, NoteBreakdown? Upper);

/// <summary>Bounded knapsack: each reachable amount retains its minimum-note path.</summary>
public sealed class NoteSelector
{
    private const int AmountUnit = 10;
    private sealed record Path(Path? Previous, int Denomination, int Count);

    public WithdrawalSelection Select(IReadOnlyCollection<NoteStock> stock, int requestedAmount, int maximumAmount)
    {
        if (requestedAmount <= 0 || maximumAmount < 0)
            throw new DomainException("invalid_withdrawal_amount", "Requested amount must be positive and the limit cannot be negative.");
        if (stock.Any(item => !Banknotes.IsSupported(item.Denomination) || item.Count < 0)
            || stock.Sum(item => (long)item.Count) > 4 * 100)
            throw new DomainException("invalid_note_stock", "Provide supported denominations and a valid recycling inventory.");

        var total = stock.Sum(item => item.Denomination * item.Count);
        var bound = Math.Min(total, maximumAmount) / AmountUnit;
        var minimumNotes = Enumerable.Repeat(int.MaxValue, bound + 1).ToArray();
        var paths = new Path?[bound + 1];
        minimumNotes[0] = 0;

        foreach (var item in stock.OrderByDescending(item => item.Denomination))
        {
            // Binary bundles preserve bounded supply while reducing the number of DP passes.
            var remaining = item.Count;
            for (var size = 1; remaining > 0; size *= 2)
            {
                var count = Math.Min(size, remaining);
                remaining -= count;
                var value = item.Denomination / AmountUnit * count;
                for (var amount = bound; amount >= value; amount--)
                {
                    var previous = amount - value;
                    if (minimumNotes[previous] == int.MaxValue
                        || minimumNotes[previous] + count >= minimumNotes[amount])
                        continue;
                    minimumNotes[amount] = minimumNotes[previous] + count;
                    paths[amount] = new Path(paths[previous], item.Denomination, count);
                }
            }
        }

        if (requestedAmount % AmountUnit == 0 && requestedAmount / AmountUnit <= bound
            && minimumNotes[requestedAmount / AmountUnit] != int.MaxValue)
            return new WithdrawalSelection(Build(requestedAmount / AmountUnit), null, null);

        NoteBreakdown? lower = null;
        NoteBreakdown? upper = null;
        for (var amount = Math.Min(bound, (requestedAmount - 1) / AmountUnit); amount > 0; amount--)
        {
            if (minimumNotes[amount] == int.MaxValue) continue;
            lower = Build(amount);
            break;
        }
        // Avoid integer overflow for an extreme requested amount.
        for (var amount = (long)requestedAmount / AmountUnit + 1; amount <= bound; amount++)
        {
            if (minimumNotes[amount] == int.MaxValue) continue;
            upper = Build((int)amount);
            break;
        }
        return new WithdrawalSelection(null, lower, upper);

        NoteBreakdown Build(int amount)
        {
            var notes = new Dictionary<int, int>();
            for (var path = paths[amount]; path is not null; path = path.Previous)
                notes[path.Denomination] = notes.GetValueOrDefault(path.Denomination) + path.Count;
            return new NoteBreakdown(amount * AmountUnit, notes);
        }
    }
}
