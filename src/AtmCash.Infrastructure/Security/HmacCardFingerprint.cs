using System.Security.Cryptography;
using System.Text;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Common;

namespace AtmCash.Infrastructure.Security;

public sealed class HmacCardFingerprint : ICardFingerprint
{
    private readonly byte[] _key;

    public HmacCardFingerprint(string base64Key)
    {
        try { _key = Convert.FromBase64String(base64Key); }
        catch (FormatException) { throw new InvalidOperationException("Security:CardHashKey must be a Base64-encoded key."); }
        if (_key.Length < 32)
            throw new InvalidOperationException("Security:CardHashKey must contain at least 32 bytes.");
    }

    public string Compute(string cardNumber)
    {
        if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length is < 13 or > 19
            || cardNumber.Any(character => character is < '0' or > '9'))
            throw new DomainException("invalid_card_number", "Provide a card number containing 13–19 digits.");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(cardNumber)));
    }
}
