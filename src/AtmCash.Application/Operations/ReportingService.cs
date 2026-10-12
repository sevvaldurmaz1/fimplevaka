using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;

namespace AtmCash.Application.Operations;

public sealed class ReportingService(IOperationsStore store)
{
    public async Task<OperationsReport> ReportAsync(Guid? atmId, CancellationToken token)
    {
        var snapshot = await store.ReadReportAsync(atmId, token);
        var atms = snapshot.Atms.Select(atm => Map(atm, snapshot.Rates)).ToArray();
        var totals = Enum.GetValues<Currency>().Select(currency =>
        {
            var balances = atms.SelectMany(atm => atm.Totals).Where(balance => balance.Currency == currency).ToArray();
            return new CurrencyBalance(currency, balances.Sum(item => item.NoteCount), balances.Sum(item => item.ReservedCount),
                balances.Sum(item => item.AvailableCount), balances.Sum(item => item.Balance), balances.Sum(item => item.ReservedBalance),
                balances.Sum(item => item.AvailableBalance), ExchangeRate.Require(snapshot.Rates, currency));
        }).ToArray();
        return new(atms, totals, atms.Sum(atm => atm.TotalInTry), atms.Sum(atm => atm.ReservedTotalInTry), atms.Sum(atm => atm.AvailableTotalInTry));
    }

    public async Task<ConsistencyView> ConsistencyAsync(Guid atmId, CancellationToken token)
    {
        var snapshot = await store.ReadConsistencyAsync(atmId, token);
        var stored = snapshot.Atm.Cassettes.SelectMany(cassette => cassette.Inventory)
            .ToDictionary(note => (note.CassetteId, note.Currency, note.Denomination));
        var calculated = snapshot.Balances.ToDictionary(balance => (balance.CassetteId, balance.Currency, balance.Denomination));
        var differences = new List<BalanceDifference>();
        foreach (var key in stored.Keys.Union(calculated.Keys))
        {
            stored.TryGetValue(key, out var inventory);
            calculated.TryGetValue(key, out var balance);
            if ((inventory?.NoteCount ?? 0) == (balance?.NoteCount ?? 0) && (inventory?.ReservedCount ?? 0) == (balance?.ReservedCount ?? 0)) continue;
            differences.Add(new(key.CassetteId, snapshot.Atm.Cassettes.Single(cassette => cassette.Id == key.CassetteId).Number,
                key.Currency, key.Denomination, inventory?.NoteCount ?? 0, balance?.NoteCount ?? 0,
                inventory?.ReservedCount ?? 0, balance?.ReservedCount ?? 0));
        }
        return new(atmId, differences.Count == 0, differences.OrderBy(item => item.CassetteNumber).ThenBy(item => item.Currency).ThenBy(item => item.Denomination).ToArray());
    }

    private static AtmReport Map(Atm atm, IReadOnlyDictionary<Currency, decimal> rates)
    {
        var notes = atm.Cassettes.SelectMany(cassette => cassette.Inventory).ToArray();
        var totals = Enum.GetValues<Currency>().Select(currency =>
        {
            var items = notes.Where(note => note.Currency == currency).ToArray();
            return new CurrencyBalance(currency, items.Sum(note => (long)note.NoteCount), items.Sum(note => (long)note.ReservedCount),
                items.Sum(note => (long)note.AvailableCount), items.Sum(note => (long)note.NoteCount * note.Denomination),
                items.Sum(note => (long)note.ReservedCount * note.Denomination), items.Sum(note => (long)note.AvailableCount * note.Denomination),
                ExchangeRate.Require(rates, currency));
        }).ToArray();
        var cassettes = atm.ToView().Cassettes.Select(cassette => new CassetteReport(cassette.Id, cassette.Number, cassette.Type,
            cassette.Currency, cassette.Denomination, cassette.Capacity, cassette.NoteCount,
            cassette.Inventory.Sum(note => note.ReservedCount), cassette.Inventory.Sum(note => note.AvailableCount), cassette.Inventory)).ToArray();
        return new(atm.Id, atm.Name, atm.State, atm.PendingReconciliationId, cassettes, totals,
            totals.Sum(item => item.Balance * item.TryRate), totals.Sum(item => item.ReservedBalance * item.TryRate), totals.Sum(item => item.AvailableBalance * item.TryRate));
    }
}
