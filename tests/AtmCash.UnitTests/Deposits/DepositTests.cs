using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;
using AtmCash.Domain.Ledger;

namespace AtmCash.UnitTests.Deposits;

public sealed class DepositTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MatchingNotesGoToRecyclingAndUnmatchedNotesGoToDepositCassette()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        var result = atm.Deposit([new(Currency.TRY, 100, 2), new(Currency.EUR, 50, 3)], 0, Now);
        Assert.Collection(result.Accepted,
            note => { Assert.Equal(1, note.CassetteNumber); Assert.Equal(2, note.Count); },
            note => { Assert.Equal(5, note.CassetteNumber); Assert.Equal(3, note.Count); });
        Assert.Empty(result.Returned);
        Assert.Equal(3, Cassette(atm, 1).NoteCount);
        Assert.Equal(3, Cassette(atm, 5).NoteCount);
    }

    [Fact]
    public void RecyclingOverflowGoesToDepositThenExcessIsReturned()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 98), new(5, Currency.EUR, 20, 99)]);
        var result = atm.Deposit([new(Currency.TRY, 100, 5)], 2, Now);
        Assert.Collection(result.Accepted,
            note => { Assert.Equal(1, note.CassetteNumber); Assert.Equal(2, note.Count); },
            note => { Assert.Equal(5, note.CassetteNumber); Assert.Equal(1, note.Count); });
        Assert.Equal(2, Assert.Single(result.Returned).Count);
        Assert.Equal(2, result.UnrecognizedReturnedCount);
        Assert.Equal(100, Cassette(atm, 1).NoteCount);
        Assert.Equal(100, Cassette(atm, 5).NoteCount);
    }

    [Fact]
    public void AllMatchingRecyclingCassettesAreUsedBeforeDepositCassette()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 99), new(2, Currency.TRY, 100, 98)],
            [new(1, Currency.TRY, 100), new(2, Currency.TRY, 100), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)]);
        var result = atm.Deposit([new(Currency.TRY, 100, 5)], 0, Now);
        Assert.Equal(new[] { 1, 2, 5 }, result.Accepted.Select(note => note.CassetteNumber));
        Assert.Equal(new[] { 1, 2, 2 }, result.Accepted.Select(note => note.Count));
        Assert.Empty(result.Returned);
    }

    [Fact]
    public void DepositCapacityIsSharedByAllCurrenciesAndFirstInputGroupGetsRemainingSpace()
    {
        var atm = Setup([new(5, Currency.TRY, 500, 99)]);
        var result = atm.Deposit([new(Currency.EUR, 100, 1), new(Currency.USD, 20, 1)], 0, Now);
        Assert.Equal(Currency.EUR, Assert.Single(result.Accepted).Currency);
        Assert.Equal(Currency.USD, Assert.Single(result.Returned).Currency);
        Assert.Equal(100, Cassette(atm, 5).NoteCount);
    }

    [Fact]
    public void ReservedNotesStillOccupyPhysicalCapacity()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 100)]);
        var recycling = Cassette(atm, 1);
        atm.ReserveNotes(Currency.TRY, [new(recycling.Id, 100, 100)], Now, Guid.NewGuid());
        var result = atm.Deposit([new(Currency.TRY, 100, 2)], 0, Now);
        Assert.Equal(5, Assert.Single(result.Accepted).CassetteNumber);
        Assert.Equal(100, recycling.NoteCount);
        Assert.Equal(100, recycling.ReservedCount);
        Assert.Equal(0, Assert.Single(recycling.Inventory).AvailableCount);
    }

    [Fact]
    public void DuplicateGroupsAreMergedWithoutIntegerOverflow()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 99), new(5, Currency.EUR, 20, 100)]);
        var result = atm.Deposit([new(Currency.TRY, 100, int.MaxValue), new(Currency.TRY, 100, int.MaxValue)], 0, Now);
        Assert.Equal(1, Assert.Single(result.Accepted).Count);
        Assert.Equal((long)int.MaxValue * 2 - 1, Assert.Single(result.Returned).Count);
    }

    [Fact]
    public void RecognizedUnrecognizedAndReturnedNotesConserveInputCounts()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 99), new(5, Currency.EUR, 20, 99)]);
        NoteBatch[] notes = [new(Currency.TRY, 100, 7), new(Currency.EUR, 500, 2), new(Currency.TRY, 100, 3)];
        var result = atm.Deposit(notes, 4, Now);
        Assert.Equal(notes.Sum(note => (long)note.Count), result.Accepted.Sum(note => (long)note.Count) + result.Returned.Sum(note => note.Count));
        Assert.Equal(4, result.UnrecognizedReturnedCount);
        Assert.Equal(100, Assert.Single(result.Returned, note => note.Currency == Currency.TRY).Denomination);
        Assert.Equal(10, result.Accepted.Where(note => note.Currency == Currency.TRY).Sum(note => note.Count)
            + Assert.Single(result.Returned, note => note.Currency == Currency.TRY).Count);
    }

    [Fact]
    public void OnlyUnrecognizedNotesDoNotCreateInventoryOrMovements()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        var before = atm.Cassettes.Sum(cassette => cassette.Movements.Count);
        var result = atm.Deposit([], 3, Now);
        Assert.Empty(result.Accepted);
        Assert.Empty(result.Returned);
        Assert.Equal(3, result.UnrecognizedReturnedCount);
        Assert.Equal(1, atm.Cassettes.Sum(cassette => cassette.NoteCount));
        Assert.Equal(before, atm.Cassettes.Sum(cassette => cassette.Movements.Count));
    }

    [Fact]
    public void FullAtmReturnsAllNotesWithoutDepositMovement()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 100), new(5, Currency.EUR, 20, 100)]);
        var result = atm.Deposit([new(Currency.TRY, 100, 2), new(Currency.EUR, 500, 3)], 1, Now);
        Assert.Empty(result.Accepted);
        Assert.Equal(5, result.Returned.Sum(note => note.Count));
        Assert.DoesNotContain(atm.Cassettes.SelectMany(cassette => cassette.Movements), movement => movement.Type == MovementType.DEPOSIT);
    }

    [Fact]
    public void AcceptedNotesCreateCorrelatedDepositMovementsOnly()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 99), new(5, Currency.EUR, 20, 99)]);
        var result = atm.Deposit([new(Currency.TRY, 100, 5)], 2, Now);
        var movements = atm.Cassettes.SelectMany(cassette => cassette.Movements)
            .Where(movement => movement.Type == MovementType.DEPOSIT).ToArray();
        Assert.Equal(2, movements.Length);
        Assert.All(movements, movement =>
        {
            Assert.Equal(result.Id, movement.ReferenceId);
            Assert.Equal(0, movement.ReservedDelta);
            Assert.Equal(Now, movement.OccurredAt);
        });
        Assert.Equal(result.Accepted.Sum(note => note.Count), movements.Sum(movement => movement.NoteDelta));
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void InvalidLaterGroupCannotPartiallyMutateInventoryOrLedger(int denomination, int count)
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        var before = atm.Cassettes.Sum(cassette => cassette.Movements.Count);
        Assert.Throws<DomainException>(() => atm.Deposit([new(Currency.TRY, 100, 1), new(Currency.EUR, denomination, count)], 0, Now));
        Assert.Equal(1, atm.Cassettes.Sum(cassette => cassette.NoteCount));
        Assert.Equal(before, atm.Cassettes.Sum(cassette => cassette.Movements.Count));
    }

    [Fact]
    public void UnsupportedCurrencyIsRejectedBeforeMutation()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        Assert.Equal("unsupported_currency", Assert.Throws<DomainException>(() =>
            atm.Deposit([new((Currency)999, 100, 1)], 0, Now)).Code);
    }

    [Fact]
    public void EmptyDepositNegativeUnrecognizedCountAndNullGroupsAreRejected()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        Assert.Equal("empty_deposit", Assert.Throws<DomainException>(() => atm.Deposit([], 0, Now)).Code);
        Assert.Equal("invalid_unrecognized_count", Assert.Throws<DomainException>(() => atm.Deposit([], -1, Now)).Code);
        Assert.Equal("invalid_deposit_notes", Assert.Throws<DomainException>(() => atm.Deposit([null!], 0, Now)).Code);
        Assert.Equal("invalid_deposit_notes", Assert.Throws<DomainException>(() =>
            atm.Deposit(Enumerable.Repeat(new NoteBatch(Currency.TRY, 100, 1), DepositResult.MaximumNoteGroups + 1).ToArray(), 0, Now)).Code);
    }

    [Theory]
    [InlineData(AtmState.UNCONFIGURED)]
    [InlineData(AtmState.CONFIGURED)]
    [InlineData(AtmState.LOADED)]
    [InlineData(AtmState.MAINTENANCE)]
    public void DepositsRequireInServiceState(AtmState state)
    {
        var atm = Atm.Create("ATM");
        if (state != AtmState.UNCONFIGURED) atm.Configure(Configuration);
        if (state is AtmState.LOADED or AtmState.MAINTENANCE) atm.Load([new(1, Currency.TRY, 100, 1)], Now);
        if (state == AtmState.MAINTENANCE)
        {
            atm.ChangeState(AtmState.IN_SERVICE);
            atm.ChangeState(AtmState.MAINTENANCE);
        }
        Assert.Equal("invalid_atm_state", Assert.Throws<DomainException>(() => atm.Deposit([new(Currency.TRY, 100, 1)], 0, Now)).Code);
    }

    private static CassetteConfiguration[] Configuration =>
        [new(1, Currency.TRY, 100), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)];

    private static Atm Setup(CashLoad[] loads, CassetteConfiguration[]? configurations = null)
    {
        var atm = Atm.Create("Deposit ATM");
        atm.Configure(configurations ?? Configuration);
        atm.Load(loads, Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        return atm;
    }

    private static Cassette Cassette(Atm atm, int number) => atm.Cassettes.Single(cassette => cassette.Number == number);
}
