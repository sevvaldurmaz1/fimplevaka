using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Atms;

public sealed class CassetteInventory
{
    private CassetteInventory() { }

    internal CassetteInventory(Guid cassetteId, Currency currency, int denomination)
    {
        CassetteId = cassetteId;
        Currency = currency;
        Denomination = denomination;
    }

    public Guid CassetteId { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public int NoteCount { get; private set; }
    public int ReservedCount { get; private set; }
    public int AvailableCount => NoteCount - ReservedCount;

    internal void AddNotes(int count) => NoteCount += count;
    internal void Reserve(int count) => ReservedCount += count;
    internal void Release(int count) => ReservedCount -= count;
    internal void RemoveNotes(int count) => NoteCount -= count;
}
