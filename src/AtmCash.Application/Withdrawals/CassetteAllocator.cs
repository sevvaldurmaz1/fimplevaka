using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Application.Withdrawals;

public sealed class CassetteAllocator
{
    public IReadOnlyList<CassetteAllocation> Allocate(Atm atm, Currency currency, NoteBreakdown breakdown)
    {
        var allocations = new List<CassetteAllocation>();
        foreach (var (denomination, count) in breakdown.Notes.OrderByDescending(item => item.Key))
        {
            var remaining = count;
            // All capacities are 100, so note count orders by physical fill ratio.
            var cassettes = atm.Cassettes.Where(item => item.Type == CassetteType.Recycling
                    && item.Currency == currency && item.Denomination == denomination)
                .OrderByDescending(item => item.NoteCount).ThenBy(item => item.Number);
            foreach (var cassette in cassettes)
            {
                var available = cassette.Inventory.Sum(item => item.AvailableCount);
                var take = Math.Min(remaining, available);
                if (take == 0) continue;
                allocations.Add(new CassetteAllocation(cassette.Id, denomination, take));
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining != 0)
                throw new DomainException("insufficient_available_notes", "The selected banknotes are no longer available.");
        }
        return allocations;
    }
}
