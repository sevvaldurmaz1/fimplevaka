using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Common;

namespace AtmCash.UnitTests.Withdrawals;

public sealed class NoteSelectorTests
{
    private readonly NoteSelector _selector = new();

    [Fact]
    public void Acceptance1_GreedyTrap_Pays160WithFiveNotes()
        => AssertExact([new(100, 0), new(50, 3), new(20, 10)], 160, new Dictionary<int, int> { [50] = 2, [20] = 3 });

    [Fact]
    public void Acceptance2_Request80_Offers70And90()
        => AssertAlternatives([new(50, 1), new(20, 2)], 80,
            new(70, new Dictionary<int, int> { [50] = 1, [20] = 1 }),
            new(90, new Dictionary<int, int> { [50] = 1, [20] = 2 }));

    [Fact]
    public void Acceptance3_Request130_Offers120And140()
        => AssertAlternatives([new(100, 5), new(20, 10)], 130,
            new(120, new Dictionary<int, int> { [100] = 1, [20] = 1 }),
            new(140, new Dictionary<int, int> { [100] = 1, [20] = 2 }));

    [Fact]
    public void Acceptance4_UsdRequest250_Offers200WithoutUpper()
        => AssertAlternatives([new(100, 2)], 250,
            new(200, new Dictionary<int, int> { [100] = 2 }), null, 1000);

    [Fact]
    public void Acceptance5_Request600_Pays500AndFive20s()
        => AssertExact([new(500, 1), new(100, 0), new(20, 5)], 600,
            new Dictionary<int, int> { [500] = 1, [20] = 5 });

    [Fact]
    public void Acceptance6_Request200_UsesThreeNotes()
        => AssertExact([new(100, 1), new(50, 4), new(20, 5)], 200,
            new Dictionary<int, int> { [100] = 1, [50] = 2 });

    [Fact]
    public void Acceptance7_Request1000_UsesFourteenNotes()
        => AssertExact([new(500, 1), new(100, 3), new(20, 20)], 1000,
            new Dictionary<int, int> { [500] = 1, [100] = 3, [20] = 10 });

    [Fact]
    public void Acceptance8_Request110_Offers100And120()
        => AssertAlternatives([new(100, 3), new(50, 1), new(20, 1)], 110,
            new(100, new Dictionary<int, int> { [100] = 1 }),
            new(120, new Dictionary<int, int> { [100] = 1, [20] = 1 }));

    [Fact]
    public void UpperAlternativeCannotExceedRemainingDailyLimit()
        => AssertAlternatives([new(50, 1), new(20, 2)], 80,
            new(70, new Dictionary<int, int> { [50] = 1, [20] = 1 }), null, 80);

    [Fact]
    public void NoStockReturnsNoAlternatives()
        => Assert.Equal(new WithdrawalSelection(null, null, null), _selector.Select([], 80, 20000));

    [Fact]
    public void RequestBelowSmallestNoteDoesNotOfferZero()
        => AssertAlternatives([new(20, 1)], 1, null, new(20, new Dictionary<int, int> { [20] = 1 }));

    [Fact]
    public void DuplicateDenominationsShareAvailableStock()
        => AssertExact([new(100, 1), new(100, 1)], 200, new Dictionary<int, int> { [100] = 2 });

    [Fact]
    public void ExtremeRequestDoesNotOverflow()
        => AssertAlternatives([new(500, 1)], int.MaxValue,
            new(500, new Dictionary<int, int> { [500] = 1 }), null);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveAmountsAreRejected(int amount)
        => Assert.Throws<DomainException>(() => _selector.Select([], amount, 20000));

    [Fact]
    public void MinimumNoteSelectionMatchesExhaustiveSmallInventoryOracle()
    {
        // An independent enumeration verifies optimality and bounded supply, not the DP implementation.
        var payable = new Dictionary<int, int>();
        for (var twenties = 0; twenties <= 2; twenties++)
        for (var fifties = 0; fifties <= 2; fifties++)
        for (var hundreds = 0; hundreds <= 2; hundreds++)
        {
            var amount = twenties * 20 + fifties * 50 + hundreds * 100;
            var count = twenties + fifties + hundreds;
            payable[amount] = Math.Min(payable.GetValueOrDefault(amount, int.MaxValue), count);
        }

        for (var requested = 1; requested <= 350; requested++)
        {
            var selection = _selector.Select([new(20, 2), new(50, 2), new(100, 2)], requested, 350);
            if (payable.TryGetValue(requested, out var count))
            {
                Assert.NotNull(selection.Exact);
                Assert.Equal(count, selection.Exact.Notes.Values.Sum());
                VerifySupply(selection.Exact);
            }
            else
            {
                Assert.Null(selection.Exact);
                var lower = payable.Keys.Where(value => value > 0 && value < requested).DefaultIfEmpty().Max();
                var upper = payable.Keys.Where(value => value > requested).DefaultIfEmpty().Min();
                Assert.Equal(lower == 0 ? null : (int?)lower, selection.Lower?.Amount);
                Assert.Equal(upper == 0 ? null : (int?)upper, selection.Upper?.Amount);
                foreach (var alternative in new[] { selection.Lower, selection.Upper }.OfType<NoteBreakdown>())
                {
                    Assert.Equal(payable[alternative.Amount], alternative.Notes.Values.Sum());
                    VerifySupply(alternative);
                }
            }
        }

        static void VerifySupply(NoteBreakdown breakdown)
        {
            Assert.Equal(breakdown.Amount, breakdown.Notes.Sum(item => item.Key * item.Value));
            Assert.All(breakdown.Notes.Values, count => Assert.InRange(count, 1, 2));
        }
    }

    private void AssertExact(NoteStock[] stock, int amount, Dictionary<int, int> expected)
    {
        var selection = _selector.Select(stock, amount, 20000);
        AssertBreakdown(new(amount, expected), selection.Exact);
        Assert.Null(selection.Lower);
        Assert.Null(selection.Upper);
    }

    private void AssertAlternatives(NoteStock[] stock, int amount, NoteBreakdown? lower, NoteBreakdown? upper, int limit = 20000)
    {
        var selection = _selector.Select(stock, amount, limit);
        Assert.Null(selection.Exact);
        AssertBreakdown(lower, selection.Lower);
        AssertBreakdown(upper, selection.Upper);
    }

    private static void AssertBreakdown(NoteBreakdown? expected, NoteBreakdown? actual)
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Notes.OrderBy(item => item.Key), actual.Notes.OrderBy(item => item.Key));
    }
}
