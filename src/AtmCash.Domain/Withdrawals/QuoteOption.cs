namespace AtmCash.Domain.Withdrawals;

public sealed class QuoteOption
{
    private readonly List<QuoteNoteAllocation> _allocations = [];
    private QuoteOption() { }

    internal QuoteOption(Guid quoteId, QuoteOffer offer)
    {
        Id = Guid.NewGuid();
        QuoteId = quoteId;
        Kind = offer.Kind;
        Amount = offer.Amount;
        foreach (var note in offer.Notes)
            _allocations.Add(new QuoteNoteAllocation(Id, note));
    }

    public Guid Id { get; private set; }
    public Guid QuoteId { get; private set; }
    public QuoteOptionKind Kind { get; private set; }
    public int Amount { get; private set; }
    public IReadOnlyCollection<QuoteNoteAllocation> Allocations => _allocations.AsReadOnly();
    public IReadOnlyList<CassetteAllocation> GetNotes()
        => _allocations.Select(item => new CassetteAllocation(item.CassetteId, item.Denomination, item.Count)).ToArray();
}

public sealed class QuoteNoteAllocation
{
    private QuoteNoteAllocation() { }

    internal QuoteNoteAllocation(Guid optionId, CassetteAllocation note)
    {
        OptionId = optionId;
        CassetteId = note.CassetteId;
        Denomination = note.Denomination;
        Count = note.Count;
    }

    public Guid OptionId { get; private set; }
    public Guid CassetteId { get; private set; }
    public int Denomination { get; private set; }
    public int Count { get; private set; }
}
