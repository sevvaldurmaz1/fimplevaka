using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Ledger;
using AtmCash.Domain.Reconciliation;

namespace AtmCash.UnitTests.Operations;

public sealed class OperationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<Currency, decimal> Rates = new Dictionary<Currency, decimal>
        { [Currency.TRY] = 1m, [Currency.USD] = 35m, [Currency.EUR] = 40m };

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void ApprovalThresholdIsStrictlyGreaterThanOneThousand(int missingNotes, bool needsApproval)
    {
        var atm = Setup([new(1, Currency.TRY, 100, 20)]);
        var counts = Counts(atm);
        counts[0] = new(1, [new(Currency.TRY, 100, 20 - missingNotes)]);
        var reconciliation = CashReconciliation.Create(atm, counts, Rates, "operator", Now);
        Assert.Equal(missingNotes * 100m, reconciliation.TotalDifferenceTry);
        Assert.Equal(needsApproval ? ReconciliationStatus.AWAITING_APPROVAL : ReconciliationStatus.APPLIED, reconciliation.Status);
    }

    [Fact]
    public void OppositeDifferencesDoNotCancelApprovalThreshold()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 20), new(2, Currency.TRY, 50, 20)]);
        var counts = Counts(atm);
        counts[0] = new(1, [new(Currency.TRY, 100, 10)]);
        counts[1] = new(2, [new(Currency.TRY, 50, 40)]);
        var reconciliation = CashReconciliation.Create(atm, counts, Rates, "operator", Now);
        Assert.Equal(2000m, reconciliation.TotalDifferenceTry);
        Assert.Equal(ReconciliationStatus.AWAITING_APPROVAL, reconciliation.Status);
    }

    [Fact]
    public void ForeignDifferenceUsesRateSnapshotAndRequiresDifferentApprover()
    {
        var atm = Setup([new(4, Currency.USD, 100, 2)]);
        var counts = Counts(atm);
        counts[3] = new(4, [new(Currency.USD, 100, 1)]);
        var reconciliation = CashReconciliation.Create(atm, counts, Rates, " Alice ", Now);
        Assert.Equal(3500m, reconciliation.TotalDifferenceTry);
        atm.AwaitReconciliationApproval(reconciliation);
        Assert.Equal(2, atm.Cassettes.Single(cassette => cassette.Number == 4).NoteCount);
        Assert.Equal("same_user_approval", Assert.Throws<DomainException>(() => atm.ApplyReconciliation(reconciliation, Now, "alice")).Code);
        Assert.Throws<DomainException>(() => atm.ApplyReconciliation(reconciliation, Now));
        atm.ApplyReconciliation(reconciliation, Now, "Bob");
        Assert.Equal("Bob", reconciliation.ApprovedBy);
        Assert.Null(atm.PendingReconciliationId);
        Assert.Equal(1, atm.Cassettes.Single(cassette => cassette.Number == 4).NoteCount);
        Assert.Single(atm.Cassettes.SelectMany(cassette => cassette.Movements), movement => movement.Type == MovementType.RECONCILIATION_ADJUSTMENT);
        Assert.Throws<DomainException>(() => atm.ApplyReconciliation(reconciliation, Now, "Carol"));
    }

    [Fact]
    public void PendingApprovalFreezesCashConfigurationAndServiceTransition()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 20)]);
        var counts = Counts(atm);
        counts[0] = new(1, [new(Currency.TRY, 100, 9)]);
        var reconciliation = CashReconciliation.Create(atm, counts, Rates, "operator", Now);
        atm.AwaitReconciliationApproval(reconciliation);
        Assert.Throws<DomainException>(() => atm.Load([new(1, Currency.TRY, 100, 1)], Now));
        Assert.Throws<DomainException>(() => atm.Unload([new(1, Currency.TRY, 100, 1)], Now));
        Assert.Throws<DomainException>(() => atm.Configure(Configuration));
        Assert.Throws<DomainException>(() => atm.ChangeState(AtmState.IN_SERVICE));
        Assert.Throws<DomainException>(() => CashReconciliation.Create(atm, counts, Rates, "another", Now));
    }

    [Fact]
    public void SmallDifferenceAppliesAtomicallyAndMovementReproducesBalance()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 20), new(5, Currency.EUR, 20, 1)]);
        var counts = Counts(atm);
        counts[0] = new(1, [new(Currency.TRY, 100, 19)]);
        counts[4] = new(5, [new(Currency.EUR, 20, 1), new(Currency.TRY, 20, 1)]);
        var reconciliation = CashReconciliation.Create(atm, counts, Rates, "operator", Now);
        atm.ApplyReconciliation(reconciliation, Now);
        Assert.Equal(120m, reconciliation.TotalDifferenceTry);
        Assert.Equal(Now, reconciliation.AppliedAt);
        Assert.Null(reconciliation.ApprovedBy);
        foreach (var cassette in atm.Cassettes)
            Assert.Equal(cassette.NoteCount, cassette.Movements.Sum(movement => movement.NoteDelta));
    }

    [Fact]
    public void UnchangedCountCreatesAuditWithoutZeroMovements()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 1)]);
        var reconciliation = CashReconciliation.Create(atm, Counts(atm), Rates, "operator", Now);
        atm.ApplyReconciliation(reconciliation, Now);
        Assert.Equal(0m, reconciliation.TotalDifferenceTry);
        Assert.DoesNotContain(atm.Cassettes.SelectMany(cassette => cassette.Movements), movement => movement.Type == MovementType.RECONCILIATION_ADJUSTMENT);
    }

    [Fact]
    public void InvalidCountsAndMissingRateAreRejectedWithoutMutation()
    {
        var atm = Setup([new(4, Currency.USD, 100, 1)]);
        Assert.Throws<DomainException>(() => CashReconciliation.Create(atm, Counts(atm).Take(4).ToArray(), Rates, "user", Now));
        var counts = Counts(atm);
        counts[4] = new(5, [new(Currency.TRY, 20, 60), new(Currency.EUR, 50, 41)]);
        Assert.Throws<DomainException>(() => CashReconciliation.Create(atm, counts, Rates, "user", Now));
        counts = Counts(atm);
        counts[0] = new(1, [new(Currency.EUR, 100, 1)]);
        Assert.Throws<DomainException>(() => CashReconciliation.Create(atm, counts, Rates, "user", Now));
        Assert.Throws<DomainException>(() => CashReconciliation.Create(atm, Counts(atm), new Dictionary<Currency, decimal>(), "user", Now));
        Assert.Equal(1, atm.Cassettes.Sum(cassette => cassette.NoteCount));
    }

    [Fact]
    public void UnloadBatchCannotDebitTwiceBeyondInventory()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 10)]);
        Assert.Throws<DomainException>(() => atm.Unload([new(1, Currency.TRY, 100, 6), new(1, Currency.TRY, 100, 5)], Now));
        Assert.Equal(10, atm.Cassettes.Sum(cassette => cassette.NoteCount));
        atm.Unload([new(1, Currency.TRY, 100, 10)], Now);
        Assert.Equal(0, atm.Cassettes.Sum(cassette => cassette.NoteCount));
        Assert.Equal(-10, Assert.Single(atm.Cassettes.SelectMany(cassette => cassette.Movements), movement => movement.Type == MovementType.UNLOAD).NoteDelta);
        atm.Configure([new(1, Currency.EUR, 100), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)]);
    }

    [Fact]
    public void LowCashAlertUsesAvailableNotesAndRearmsAtTen()
    {
        var atm = Setup([new(1, Currency.TRY, 100, 10), new(2, Currency.TRY, 50, 10), new(3, Currency.TRY, 20, 10), new(4, Currency.USD, 100, 10)]);
        atm.ChangeState(AtmState.IN_SERVICE);
        Assert.Empty(atm.RefreshLowCashAlerts(Now));
        var cassette = atm.Cassettes.Single(item => item.Number == 1);
        var quoteId = Guid.NewGuid();
        atm.ReserveNotes(Currency.TRY, [new(cassette.Id, 100, 1)], Now, quoteId);
        Assert.Equal(9, Assert.Single(atm.RefreshLowCashAlerts(Now)).AvailableCount);
        Assert.Empty(atm.RefreshLowCashAlerts(Now));
        atm.ReleaseNotes(Currency.TRY, [new(cassette.Id, 100, 1)], Now, quoteId);
        Assert.Empty(atm.RefreshLowCashAlerts(Now));
        atm.ReserveNotes(Currency.TRY, [new(cassette.Id, 100, 1)], Now, Guid.NewGuid());
        Assert.Single(atm.RefreshLowCashAlerts(Now));
    }

    private static CassetteConfiguration[] Configuration =>
        [new(1, Currency.TRY, 100), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)];
    private static Atm Setup(CashLoad[] loads)
    {
        var atm = Atm.Create("Operations ATM");
        atm.Configure(Configuration);
        atm.Load(loads, Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        atm.ChangeState(AtmState.MAINTENANCE);
        return atm;
    }
    private static CassetteCount[] Counts(Atm atm) => atm.Cassettes.OrderBy(cassette => cassette.Number)
        .Select(cassette => new CassetteCount(cassette.Number, cassette.Inventory
            .Select(note => new NoteBatch(note.Currency, note.Denomination, note.NoteCount)).ToArray())).ToArray();
}
