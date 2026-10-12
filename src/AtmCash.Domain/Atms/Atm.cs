using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;
using AtmCash.Domain.Withdrawals;
using AtmCash.Domain.Reconciliation;
using AtmCash.Domain.Alerts;

namespace AtmCash.Domain.Atms;

public sealed class Atm
{
    private readonly List<Cassette> _cassettes = [];
    private Atm() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AtmState State { get; private set; } = AtmState.UNCONFIGURED;
    public bool HasCashLoaded { get; private set; }
    public Guid? PendingReconciliationId { get; private set; }
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
        EnsureNoPendingReconciliation();
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
        EnsureNoPendingReconciliation();
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

    public DepositResult Deposit(IReadOnlyCollection<NoteBatch> notes, int unrecognizedCount, DateTimeOffset now)
    {
        EnsureInService();
        if (unrecognizedCount < 0)
            throw new DomainException("invalid_unrecognized_count", "Unrecognized note count cannot be negative.");
        if (notes.Count > DepositResult.MaximumNoteGroups || notes.Any(note => note is null))
            throw new DomainException("invalid_deposit_notes", "Provide valid banknote groups, up to 60 entries.");
        if (notes.Count == 0 && unrecognizedCount == 0)
            throw new DomainException("empty_deposit", "Provide at least one recognized or unrecognized banknote.");
        // Validate the whole request before changing a cassette.
        foreach (var note in notes)
        {
            Banknotes.Validate(note.Currency, note.Denomination);
            if (note.Count <= 0)
                throw new DomainException("invalid_note_count", "Recognized note counts must be positive.");
        }

        var depositId = Guid.NewGuid();
        var accepted = new List<DepositPlacement>();
        var returned = new List<ReturnedDepositNotes>();
        var depositCassette = _cassettes.Single(cassette => cassette.Type == CassetteType.DepositOnly);
        // GroupBy retains first-occurrence order, making mixed deposits deterministic.
        foreach (var group in notes.GroupBy(note => (note.Currency, note.Denomination)))
        {
            var (currency, denomination) = group.Key;
            var remaining = group.Sum(note => (long)note.Count);
            var recyclingCassettes = _cassettes.Where(cassette => cassette.Type == CassetteType.Recycling
                    && cassette.Currency == currency && cassette.Denomination == denomination)
                .OrderBy(cassette => cassette.Number);
            foreach (var cassette in recyclingCassettes)
                PlaceInto(cassette);
            PlaceInto(depositCassette);
            if (remaining > 0)
                returned.Add(new ReturnedDepositNotes(currency, denomination, remaining));

            void PlaceInto(Cassette cassette)
            {
                var count = (int)Math.Min(remaining, Cassette.Capacity - cassette.NoteCount);
                if (count == 0) return;
                cassette.Deposit(currency, denomination, count, now, depositId);
                accepted.Add(new DepositPlacement(cassette.Id, cassette.Number, currency, denomination, count));
                remaining -= count;
            }
        }
        return new DepositResult(depositId, Id, now, accepted.AsReadOnly(), returned.AsReadOnly(), unrecognizedCount);
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
        EnsureNoPendingReconciliation();
        if (State is not (AtmState.UNCONFIGURED or AtmState.CONFIGURED or AtmState.MAINTENANCE))
            throw new DomainException("invalid_atm_state", "Configuration requires UNCONFIGURED, CONFIGURED or MAINTENANCE state.");
    }

    public void Unload(IReadOnlyCollection<CashLoad> notes, DateTimeOffset now)
    {
        EnsureSetupOrMaintenance();
        if (notes.Count == 0 || notes.Any(note => note is null))
            throw new DomainException("empty_cash_unload", "Provide at least one note to unload.");
        foreach (var note in notes)
            FindCassette(note.CassetteNumber).ValidateUnload(note.Currency, note.Denomination, note.Count);
        foreach (var group in notes.GroupBy(note => (note.CassetteNumber, note.Currency, note.Denomination)))
        {
            var available = FindCassette(group.Key.CassetteNumber).Inventory
                .Where(note => note.Currency == group.Key.Currency && note.Denomination == group.Key.Denomination)
                .Sum(note => note.AvailableCount);
            if (group.Sum(note => (long)note.Count) > available)
                throw new DomainException("insufficient_available_notes", "The unload request exceeds the cassette inventory.");
        }
        foreach (var note in notes)
            FindCassette(note.CassetteNumber).Unload(note.Currency, note.Denomination, note.Count, now);
    }

    public void ValidateReconciliation(IReadOnlyCollection<CassetteCount> counts)
    {
        EnsureSetupOrMaintenance();
        if (counts.Count != 5 || counts.Any(count => count is null || count.Notes is null)
            || counts.Select(count => count.Number).Distinct().Count() != 5)
            throw new DomainException("invalid_reconciliation_counts", "Provide physical counts for each of the five cassettes exactly once.");
        if (_cassettes.Any(cassette => cassette.ReservedCount != 0))
            throw new DomainException("active_reservations", "Release all reservations before reconciling.");
        foreach (var count in counts)
            FindCassette(count.Number).ValidateCount(count.Notes);
    }

    public void AwaitReconciliationApproval(CashReconciliation reconciliation)
    {
        EnsureSetupOrMaintenance();
        if (reconciliation.AtmId != Id || reconciliation.Status != ReconciliationStatus.AWAITING_APPROVAL)
            throw new DomainException("invalid_reconciliation", "The reconciliation does not require approval at this ATM.");
        PendingReconciliationId = reconciliation.Id;
    }

    public void ApplyReconciliation(CashReconciliation reconciliation, DateTimeOffset now, string? approver = null)
    {
        if (State is not (AtmState.UNCONFIGURED or AtmState.CONFIGURED or AtmState.MAINTENANCE))
            throw new DomainException("invalid_atm_state", "Reconciliation requires setup or MAINTENANCE state.");
        if (reconciliation.AtmId != Id || reconciliation.AppliedAt is not null
            || (PendingReconciliationId is not null && PendingReconciliationId != reconciliation.Id))
            throw new DomainException("invalid_reconciliation", "The reconciliation cannot be applied at this ATM.");
        if (reconciliation.Status == ReconciliationStatus.AWAITING_APPROVAL)
        {
            if (PendingReconciliationId != reconciliation.Id)
                throw new DomainException("invalid_reconciliation", "This reconciliation is not pending at this ATM.");
            reconciliation.ValidateApprover(approver ?? string.Empty);
        }
        foreach (var line in reconciliation.Lines)
        {
            var current = FindCassette(line.CassetteId).Inventory
                .SingleOrDefault(note => note.Currency == line.Currency && note.Denomination == line.Denomination);
            if ((current?.NoteCount ?? 0) != line.SystemCount || (current?.ReservedCount ?? 0) != 0)
                throw new DomainException("reconciliation_stale", "Inventory changed after this physical count was recorded.");
        }
        foreach (var line in reconciliation.Lines.Where(line => line.Difference != 0))
            FindCassette(line.CassetteId).Adjust(line.Currency, line.Denomination, line.Difference, now, reconciliation.Id);
        reconciliation.MarkApplied(approver, now);
        PendingReconciliationId = null;
    }

    public IReadOnlyList<LowCashAlert> RefreshLowCashAlerts(DateTimeOffset now)
        => HasCashLoaded ? _cassettes.Select(cassette => cassette.RefreshLowCashAlert(now)).OfType<LowCashAlert>().ToArray() : [];

    private void EnsureNoPendingReconciliation()
    {
        if (PendingReconciliationId is not null)
            throw new DomainException("reconciliation_awaiting_approval", "Approve the pending reconciliation before changing cash, configuration or ATM state.");
    }
}
