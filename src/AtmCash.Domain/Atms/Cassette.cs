using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Ledger;
using AtmCash.Domain.Alerts;

namespace AtmCash.Domain.Atms;

public sealed class Cassette
{
    public const int Capacity = 100;
    private readonly List<CassetteInventory> _inventory = [];
    private readonly List<CassetteMovement> _movements = [];

    private Cassette() { }

    internal Cassette(Guid atmId, int number, CassetteType type)
    {
        Id = Guid.NewGuid();
        AtmId = atmId;
        Number = number;
        Type = type;
    }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public int Number { get; private set; }
    public CassetteType Type { get; private set; }
    public Currency? Currency { get; private set; }
    public int? Denomination { get; private set; }
    public IReadOnlyCollection<CassetteInventory> Inventory => _inventory.AsReadOnly();
    public IReadOnlyCollection<CassetteMovement> Movements => _movements.AsReadOnly();
    public int NoteCount => _inventory.Sum(item => item.NoteCount);
    public int ReservedCount => _inventory.Sum(item => item.ReservedCount);
    public bool LowCashAlertActive { get; private set; }

    internal void ValidateConfiguration(Currency currency, int denomination)
    {
        Banknotes.Validate(currency, denomination);
        if (Type != CassetteType.Recycling)
            throw new DomainException("deposit_cassette_configuration", "The deposit cassette accepts all supported notes.");
        if (NoteCount > 0 && (Currency != currency || Denomination != denomination))
            throw new DomainException("cassette_not_empty", "Empty the cassette before changing its configuration.");
    }

    internal void Configure(Currency currency, int denomination)
    {
        if (Currency != currency || Denomination != denomination) LowCashAlertActive = false;
        Currency = currency;
        Denomination = denomination;
    }

    internal void ValidateLoad(Currency currency, int denomination, int count)
    {
        Banknotes.Validate(currency, denomination);
        if (count <= 0)
            throw new DomainException("invalid_note_count", "Note counts must be positive.");
        if (Type == CassetteType.Recycling && (Currency != currency || Denomination != denomination))
            throw new DomainException("cassette_note_mismatch", "The notes do not match the recycling cassette configuration.");
    }

    internal void Load(Currency currency, int denomination, int count, DateTimeOffset now)
        => AddNotes(currency, denomination, count, MovementType.LOAD, now);

    internal void Deposit(Currency currency, int denomination, int count, DateTimeOffset now, Guid depositId)
    {
        ValidateLoad(currency, denomination, count);
        if (count > Capacity - NoteCount)
            throw new DomainException("cassette_capacity_exceeded", "A cassette can hold at most 100 banknotes.");
        AddNotes(currency, denomination, count, MovementType.DEPOSIT, now, depositId);
    }

    private void AddNotes(Currency currency, int denomination, int count, MovementType movementType,
        DateTimeOffset now, Guid? referenceId = null)
    {
        var inventory = _inventory.SingleOrDefault(item => item.Currency == currency && item.Denomination == denomination);
        if (inventory is null)
        {
            inventory = new CassetteInventory(Id, currency, denomination);
            _inventory.Add(inventory);
        }
        inventory.AddNotes(count);
        _movements.Add(new CassetteMovement(Id, currency, denomination, movementType, count, 0, now, referenceId));
    }

    internal void ValidateWithdrawalNotes(Currency currency, int denomination, int count, bool requireReserved)
    {
        ValidateLoad(currency, denomination, count);
        if (Type != CassetteType.Recycling)
            throw new DomainException("deposit_cassette_withdrawal", "Notes cannot be dispensed from the deposit cassette.");
        var inventory = FindInventory(currency, denomination);
        if (count > (requireReserved ? inventory.ReservedCount : inventory.AvailableCount))
            throw new DomainException("insufficient_available_notes", "The cassette does not contain enough available or reserved notes.");
    }

    internal void Reserve(Currency currency, int denomination, int count, DateTimeOffset now, Guid quoteId)
    {
        FindInventory(currency, denomination).Reserve(count);
        _movements.Add(new CassetteMovement(Id, currency, denomination, MovementType.RESERVATION, 0, count, now, quoteId));
    }

    internal void Release(Currency currency, int denomination, int count, DateTimeOffset now, Guid quoteId)
    {
        FindInventory(currency, denomination).Release(count);
        _movements.Add(new CassetteMovement(Id, currency, denomination, MovementType.RESERVATION_RELEASE, 0, -count, now, quoteId));
    }

    internal void Dispense(Currency currency, int denomination, int count, DateTimeOffset now, Guid quoteId)
    {
        FindInventory(currency, denomination).RemoveNotes(count);
        _movements.Add(new CassetteMovement(Id, currency, denomination, MovementType.WITHDRAWAL, -count, 0, now, quoteId));
    }

    internal void ValidateUnload(Currency currency, int denomination, int count)
    {
        Banknotes.Validate(currency, denomination);
        if (count <= 0 || count > FindInventory(currency, denomination).AvailableCount)
            throw new DomainException("insufficient_available_notes", "Unload a positive number of available notes.");
    }

    internal void Unload(Currency currency, int denomination, int count, DateTimeOffset now)
    {
        FindInventory(currency, denomination).RemoveNotes(count);
        _movements.Add(new CassetteMovement(Id, currency, denomination, MovementType.UNLOAD, -count, 0, now));
    }

    internal void ValidateCount(IReadOnlyCollection<NoteBatch> notes)
    {
        if (notes.Count > 12 || notes.Any(note => note is null)
            || notes.Select(note => (note.Currency, note.Denomination)).Distinct().Count() != notes.Count)
            throw new DomainException("invalid_reconciliation_counts", "Count each supported banknote type at most once per cassette.");
        foreach (var note in notes)
        {
            Banknotes.Validate(note.Currency, note.Denomination);
            if (note.Count < 0)
                throw new DomainException("invalid_note_count", "Physical counts cannot be negative.");
            if (note.Count > 0 && Type == CassetteType.Recycling && (Currency != note.Currency || Denomination != note.Denomination))
                throw new DomainException("cassette_note_mismatch", "Counted notes must match the recycling cassette configuration.");
        }
        if (notes.Sum(note => (long)note.Count) > Capacity)
            throw new DomainException("cassette_capacity_exceeded", "A physical cassette cannot contain more than 100 notes.");
    }

    internal void Adjust(Currency currency, int denomination, int delta, DateTimeOffset now, Guid reconciliationId)
    {
        if (delta > 0)
            AddNotes(currency, denomination, delta, MovementType.RECONCILIATION_ADJUSTMENT, now, reconciliationId);
        else
        {
            FindInventory(currency, denomination).RemoveNotes(-delta);
            _movements.Add(new CassetteMovement(Id, currency, denomination, MovementType.RECONCILIATION_ADJUSTMENT, delta, 0, now, reconciliationId));
        }
    }

    internal LowCashAlert? RefreshLowCashAlert(DateTimeOffset now)
    {
        if (Type != CassetteType.Recycling || Currency is null || Denomination is null) return null;
        var available = NoteCount - ReservedCount;
        if (available >= Capacity / 10) { LowCashAlertActive = false; return null; }
        if (LowCashAlertActive) return null;
        LowCashAlertActive = true;
        return new LowCashAlert(AtmId, Id, Number, Currency.Value, Denomination.Value, available, now);
    }

    private CassetteInventory FindInventory(Currency currency, int denomination)
        => _inventory.SingleOrDefault(item => item.Currency == currency && item.Denomination == denomination)
            ?? throw new DomainException("insufficient_available_notes", "The cassette has no matching notes.");
}
