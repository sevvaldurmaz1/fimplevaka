using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.UnitTests.Withdrawals;

public sealed class CassetteAllocatorTests
{
    private readonly CassetteAllocator _allocator = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void HigherPhysicalFillRatioIsSelectedFirst()
    {
        var atm = Setup(5, 9);
        var allocation = Assert.Single(_allocator.Allocate(atm, Currency.TRY,
            new NoteBreakdown(600, new Dictionary<int, int> { [100] = 6 })));
        Assert.Equal(atm.Cassettes.Single(item => item.Number == 2).Id, allocation.CassetteId);
        Assert.Equal(6, allocation.Count);
    }

    [Fact]
    public void AlreadyReservedNotesAreExcludedWhilePhysicalFillRatioStillOrdersCassettes()
    {
        var atm = Setup(5, 9);
        var fuller = atm.Cassettes.Single(item => item.Number == 2);
        atm.ReserveNotes(Currency.TRY, [new(fuller.Id, 100, 8)], Now, Guid.NewGuid());
        var allocations = _allocator.Allocate(atm, Currency.TRY,
            new NoteBreakdown(400, new Dictionary<int, int> { [100] = 4 }));
        Assert.Equal(fuller.Id, allocations[0].CassetteId);
        Assert.Equal(1, allocations[0].Count);
        Assert.Equal(3, allocations[1].Count);
    }

    [Fact]
    public void EqualFillRatiosUseCassetteNumberAsStableTieBreak()
    {
        var atm = Setup(5, 5);
        var allocation = Assert.Single(_allocator.Allocate(atm, Currency.TRY,
            new NoteBreakdown(100, new Dictionary<int, int> { [100] = 1 })));
        Assert.Equal(atm.Cassettes.Single(item => item.Number == 1).Id, allocation.CassetteId);
    }

    [Fact]
    public void ReservationPreventsMaintenanceAndConfirmationReleasesUnusedNotes()
    {
        var atm = Setup(5, 9);
        var cassette = atm.Cassettes.Single(item => item.Number == 2);
        var quoteId = Guid.NewGuid();
        CassetteAllocation[] reserved = [new(cassette.Id, 100, 3)];
        atm.ReserveNotes(Currency.TRY, reserved, Now, quoteId);
        Assert.Throws<DomainException>(() => atm.ChangeState(AtmState.MAINTENANCE));
        atm.ConfirmWithdrawal(Currency.TRY, reserved, [new(cassette.Id, 100, 2)], Now, quoteId);
        Assert.Equal(7, cassette.NoteCount);
        Assert.Equal(0, cassette.ReservedCount);
        Assert.Equal(cassette.NoteCount, cassette.Movements.Sum(item => item.NoteDelta));
        Assert.Equal(cassette.ReservedCount, cassette.Movements.Sum(item => item.ReservedDelta));
        atm.ChangeState(AtmState.MAINTENANCE);
    }

    [Fact]
    public void ConfirmationCannotConsumeMoreThanThisQuotesReservation()
    {
        var atm = Setup(5, 9);
        var cassette = atm.Cassettes.Single(item => item.Number == 2);
        var quoteId = Guid.NewGuid();
        CassetteAllocation[] reserved = [new(cassette.Id, 100, 3)];
        atm.ReserveNotes(Currency.TRY, reserved, Now, quoteId);
        Assert.Throws<DomainException>(() => atm.ConfirmWithdrawal(Currency.TRY, reserved,
            [new(cassette.Id, 100, 4)], Now, quoteId));
        Assert.Equal(9, cassette.NoteCount);
        Assert.Equal(3, cassette.ReservedCount);
    }

    [Fact]
    public void DepositOnlyNotesAreNeverSelected()
    {
        var atm = Setup(1, 1);
        atm.ChangeState(AtmState.MAINTENANCE);
        atm.Load([new(5, Currency.TRY, 500, 1)], Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        Assert.Throws<DomainException>(() => _allocator.Allocate(atm, Currency.TRY,
            new NoteBreakdown(500, new Dictionary<int, int> { [500] = 1 })));
    }

    private static Atm Setup(int firstCount, int secondCount)
    {
        var atm = Atm.Create("Duplicate denomination ATM");
        atm.Configure([new(1, Currency.TRY, 100), new(2, Currency.TRY, 100), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)]);
        atm.Load([new(1, Currency.TRY, 100, firstCount), new(2, Currency.TRY, 100, secondCount)], Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        return atm;
    }
}
