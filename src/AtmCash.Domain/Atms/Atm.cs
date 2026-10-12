using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Domain.Atms;

public sealed class Atm
{
    private readonly List<Cassette> _cassettes = [];
    private Atm() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AtmState State { get; private set; } = AtmState.UNCONFIGURED;
    public bool HasCashLoaded { get; private set; }
    public IReadOnlyCollection<Cassette> Cassettes => _cassettes.AsReadOnly();

    public static Atm Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new DomainException("invalid_atm_name", "ATM name must contain between 1 and 100 characters.");
        var atm = new Atm { Id = Guid.NewGuid(), Name = name.Trim() };
        for (var number = 1; number <= 5; number++)
            atm._cassettes.Add(new Cassette(atm.Id, number,
                number == 5 ? CassetteType.DepositOnly : CassetteType.Recycling));
        return atm;
    }

    public void Configure(IReadOnlyCollection<CassetteConfiguration> configurations)
    {
        EnsureSetupOrMaintenance();
        if (configurations.Any(item => item is null) || configurations.Count != 4
            || configurations.Select(item => item.Number).Distinct().Count() != 4)
            throw new DomainException("invalid_cassette_configuration", "Configure all four recycling cassettes once, using numbers 1–4.");
        foreach (var configuration in configurations)
            FindCassette(configuration.Number).ValidateConfiguration(configuration.Currency, configuration.Denomination);
        var foreignCount = configurations.Count(item => item.Currency != Currency.TRY);
        if (foreignCount is < 1 or > 2)
            throw new DomainException("invalid_foreign_cassette_count", "One or two recycling cassettes must use USD or EUR.");
        foreach (var configuration in configurations)
            FindCassette(configuration.Number).Configure(configuration.Currency, configuration.Denomination);
        if (State == AtmState.UNCONFIGURED)
            State = AtmState.CONFIGURED;
    }

    public void Load(IReadOnlyCollection<CashLoad> loads, DateTimeOffset now)
    {
        if (State is not (AtmState.CONFIGURED or AtmState.MAINTENANCE))
            throw new DomainException("invalid_atm_state", "Cash loading requires CONFIGURED or MAINTENANCE state.");
        if (loads.Count == 0 || loads.Any(item => item is null))
            throw new DomainException("empty_cash_load", "Provide at least one banknote to load.");
        foreach (var load in loads)
            FindCassette(load.CassetteNumber).ValidateLoad(load.Currency, load.Denomination, load.Count);
        foreach (var group in loads.GroupBy(item => item.CassetteNumber))
        {
            if (group.Sum(item => (long)item.Count) + FindCassette(group.Key).NoteCount > Cassette.Capacity)
                throw new DomainException("cassette_capacity_exceeded", "A cassette can hold at most 100 banknotes.");
        }
        foreach (var load in loads)
            FindCassette(load.CassetteNumber).Load(load.Currency, load.Denomination, load.Count, now);
        HasCashLoaded = true;
        if (State == AtmState.CONFIGURED)
            State = AtmState.LOADED;
    }

    public void ChangeState(AtmState target)
    {
        var allowed = (State, target) switch
        {
            (AtmState.LOADED, AtmState.IN_SERVICE) => HasCashLoaded,
            (AtmState.IN_SERVICE, AtmState.MAINTENANCE) => _cassettes.All(item => item.ReservedCount == 0),
            (AtmState.MAINTENANCE, AtmState.IN_SERVICE) => HasCashLoaded,
            _ => false
        };
        if (!allowed)
            throw new DomainException("invalid_state_transition", $"Cannot change ATM state from {State} to {target}.");
        State = target;
    }

    public void EnsureInService()
    {
        if (State != AtmState.IN_SERVICE)
            throw new DomainException("invalid_atm_state", "Customer transactions require IN_SERVICE state.");
    }

    public void ReserveNotes(Currency currency, IReadOnlyCollection<CassetteAllocation> allocations,
        DateTimeOffset now, Guid quoteId)
    {
        EnsureInService();
        ValidateAllocations(currency, allocations, requireReserved: false);
        foreach (var allocation in allocations)
            FindCassette(allocation.CassetteId).Reserve(currency, allocation.Denomination, allocation.Count, now, quoteId);
    }

    public void ReleaseNotes(Currency currency, IReadOnlyCollection<CassetteAllocation> allocations,
        DateTimeOffset now, Guid quoteId)
    {
        ValidateAllocations(currency, allocations, requireReserved: true);
        foreach (var allocation in allocations)
            FindCassette(allocation.CassetteId).Release(currency, allocation.Denomination, allocation.Count, now, quoteId);
    }

    public void ConfirmWithdrawal(Currency currency, IReadOnlyCollection<CassetteAllocation> reserved,
        IReadOnlyCollection<CassetteAllocation> selected, DateTimeOffset now, Guid quoteId)
    {
        EnsureInService();
        ValidateAllocations(currency, reserved, requireReserved: true);
        ValidateAllocations(currency, selected, requireReserved: true);
        foreach (var allocation in selected)
        {
            var reservation = reserved.SingleOrDefault(item => item.CassetteId == allocation.CassetteId);
            if (reservation is null || reservation.Denomination != allocation.Denomination || allocation.Count > reservation.Count)
                throw new DomainException("invalid_withdrawal_selection", "Selected notes must be covered by this quote's reservation.");
        }
        // Release all offered notes, then debit only the selected option in the same transaction.
        ReleaseNotes(currency, reserved, now, quoteId);
        foreach (var allocation in selected)
            FindCassette(allocation.CassetteId).Dispense(currency, allocation.Denomination, allocation.Count, now, quoteId);
    }

    private void ValidateAllocations(Currency currency, IReadOnlyCollection<CassetteAllocation> allocations, bool requireReserved)
    {
        if (allocations.Count == 0 || allocations.Select(item => item.CassetteId).Distinct().Count() != allocations.Count)
            throw new DomainException("invalid_withdrawal_selection", "Provide one allocation per cassette with at least one note.");
        foreach (var allocation in allocations)
            FindCassette(allocation.CassetteId).ValidateWithdrawalNotes(currency, allocation.Denomination, allocation.Count, requireReserved);
    }

    private Cassette FindCassette(Guid id) => _cassettes.SingleOrDefault(item => item.Id == id)
        ?? throw new DomainException("invalid_cassette_id", "Cassette does not belong to this ATM.");

    private Cassette FindCassette(int number) => _cassettes.SingleOrDefault(item => item.Number == number)
        ?? throw new DomainException("invalid_cassette_number", "Cassette number must be between 1 and 5.");

    private void EnsureSetupOrMaintenance()
    {
        if (State is not (AtmState.UNCONFIGURED or AtmState.CONFIGURED or AtmState.MAINTENANCE))
            throw new DomainException("invalid_atm_state", "Configuration requires UNCONFIGURED, CONFIGURED or MAINTENANCE state.");
    }
}
