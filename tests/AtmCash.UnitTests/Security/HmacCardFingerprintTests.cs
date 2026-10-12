using AtmCash.Domain.Common;
using AtmCash.Infrastructure.Security;

namespace AtmCash.UnitTests.Security;

public sealed class HmacCardFingerprintTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    [Fact]
    public void SameCardAndKeyProduceStableFingerprintWithoutStoringPan()
    {
        const string pan = "4000000000000001";
        var fingerprints = new HmacCardFingerprint(Key);
        var hash = fingerprints.Compute(pan);
        Assert.Equal(hash, fingerprints.Compute(pan));
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(pan, hash);
        Assert.NotEqual(hash, fingerprints.Compute("4000000000000002"));
    }

    [Fact]
    public void SecretKeyChangesFingerprint()
    {
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.NotEqual(new HmacCardFingerprint(Key).Compute("4000000000000001"),
            new HmacCardFingerprint(otherKey).Compute("4000000000000001"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("YWJj")]
    public void InvalidOrShortKeyFailsStartup(string key)
        => Assert.Throws<InvalidOperationException>(() => new HmacCardFingerprint(key));

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("4000 0000 0000 0001")]
    [InlineData("40000000000000000000")]
    public void InvalidPanProducesErrorWithoutEchoingInput(string pan)
    {
        var error = Assert.Throws<DomainException>(() => new HmacCardFingerprint(Key).Compute(pan));
        Assert.Equal("invalid_card_number", error.Code);
        if (pan.Length > 0) Assert.DoesNotContain(pan, error.Message);
    }
}
