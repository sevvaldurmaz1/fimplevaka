namespace AtmCash.Domain.Withdrawals;

/// <summary>Persists a successful confirm request's identity; its result lives in the finalized quote.</summary>
public sealed class WithdrawalConfirmation
{
    private WithdrawalConfirmation() { }

    public WithdrawalConfirmation(string key, Guid quoteId, int amount)
    {
        Key = key;
        QuoteId = quoteId;
        Amount = amount;
    }

    public string Key { get; private set; } = string.Empty;
    public Guid QuoteId { get; private set; }
    public int Amount { get; private set; }
}
