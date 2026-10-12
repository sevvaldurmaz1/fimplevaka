namespace AtmCash.Application.Withdrawals;

public interface ICardFingerprint
{
    string Compute(string cardNumber);
}
