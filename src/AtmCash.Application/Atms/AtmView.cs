using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;

namespace AtmCash.Application.Atms;

public sealed record InventoryView(Currency Currency, int Denomination, int NoteCount, int ReservedCount, int AvailableCount);
public sealed record CassetteView(Guid Id, int Number, CassetteType Type, Currency? Currency,
    int? Denomination, int Capacity, int NoteCount, IReadOnlyList<InventoryView> Inventory);
public sealed record AtmView(Guid Id, string Name, AtmState State, IReadOnlyList<CassetteView> Cassettes);

public static class AtmMapping
{
    public static AtmView ToView(this Atm atm) => new(atm.Id, atm.Name, atm.State,
        atm.Cassettes.OrderBy(item => item.Number).Select(cassette => new CassetteView(
            cassette.Id, cassette.Number, cassette.Type, cassette.Currency, cassette.Denomination,
            Cassette.Capacity, cassette.NoteCount,
            cassette.Inventory.OrderBy(item => item.Currency).ThenByDescending(item => item.Denomination)
                .Select(item => new InventoryView(item.Currency, item.Denomination,
                    item.NoteCount, item.ReservedCount, item.AvailableCount)).ToArray())).ToArray());
}
