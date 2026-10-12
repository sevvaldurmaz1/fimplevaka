using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Ledger;

namespace AtmCash.UnitTests.Atms;

public sealed class AtmTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static CassetteConfiguration[] ValidConfiguration =>
        [new(1, Currency.TRY, 100), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)];

    [Fact]
    public void NewAtmHasExactlyFourRecyclingAndOneDepositCassette()
    {
        var atm = Atm.Create(" Istanbul ");
        Assert.Equal("Istanbul", atm.Name);
        Assert.Equal(AtmState.UNCONFIGURED, atm.State);
        Assert.Equal(4, atm.Cassettes.Count(item => item.Type == CassetteType.Recycling));
        Assert.Equal(5, Assert.Single(atm.Cassettes, item => item.Type == CassetteType.DepositOnly).Number);
    }

    [Fact]
    public void SetupAndMaintenanceFollowValidLifecycle()
    {
        var atm = Atm.Create("ATM");
        atm.Configure(ValidConfiguration);
        Assert.Equal(AtmState.CONFIGURED, atm.State);
        atm.Load([new(1, Currency.TRY, 100, 1)], Now);
        Assert.Equal(AtmState.LOADED, atm.State);
        atm.ChangeState(AtmState.IN_SERVICE);
        atm.ChangeState(AtmState.MAINTENANCE);
        atm.ChangeState(AtmState.IN_SERVICE);
        Assert.Equal(AtmState.IN_SERVICE, atm.State);
    }

    [Fact]
    public void AtmCannotSkipInitialConfigurationAndLoad()
    {
        var atm = Atm.Create("ATM");
        Assert.Equal("invalid_state_transition", Assert.Throws<DomainException>(() => atm.ChangeState(AtmState.IN_SERVICE)).Code);
        Assert.Throws<DomainException>(() => atm.Load([new(1, Currency.TRY, 100, 1)], Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void ForeignCassetteCountMustBeOneOrTwo(int foreignCount)
    {
        var atm = Atm.Create("ATM");
        var configurations = Enumerable.Range(1, 4)
            .Select(number => new CassetteConfiguration(number, number <= foreignCount ? Currency.USD : Currency.TRY, 100)).ToArray();
        Assert.Equal("invalid_foreign_cassette_count", Assert.Throws<DomainException>(() => atm.Configure(configurations)).Code);
        Assert.Equal(AtmState.UNCONFIGURED, atm.State);
        Assert.All(atm.Cassettes, item => Assert.Null(item.Currency));
    }

    [Fact]
    public void DuplicateCurrencyAndDenominationAreAllowed()
    {
        var atm = Atm.Create("ATM");
        atm.Configure([new(1, Currency.TRY, 100), new(2, Currency.TRY, 100), new(3, Currency.USD, 100), new(4, Currency.EUR, 100)]);
        Assert.Equal(AtmState.CONFIGURED, atm.State);
    }

    [Fact]
    public void InvalidBanknoteCannotPartiallyConfigureAtm()
    {
        var atm = Atm.Create("ATM");
        var configurations = ValidConfiguration;
        configurations[3] = new(4, Currency.USD, 10);
        Assert.Throws<DomainException>(() => atm.Configure(configurations));
        Assert.All(atm.Cassettes, item => Assert.Null(item.Currency));
    }

    [Fact]
    public void CashLoadIsAtomicAndIncludesBatchCapacity()
    {
        var atm = ConfiguredAtm();
        Assert.Equal("cassette_capacity_exceeded", Assert.Throws<DomainException>(() =>
            atm.Load([new(1, Currency.TRY, 100, 60), new(1, Currency.TRY, 100, 41)], Now)).Code);
        Assert.All(atm.Cassettes, item => Assert.Empty(item.Inventory));
        Assert.All(atm.Cassettes, item => Assert.Empty(item.Movements));
        Assert.Equal(AtmState.CONFIGURED, atm.State);
    }

    [Fact]
    public void DepositCassetteCapacityIncludesAllCurrencies()
    {
        var atm = ConfiguredAtm();
        Assert.Throws<DomainException>(() => atm.Load([new(5, Currency.TRY, 20, 60), new(5, Currency.EUR, 500, 41)], Now));
        atm.Load([new(5, Currency.TRY, 20, 60), new(5, Currency.EUR, 500, 40)], Now);
        Assert.Equal(100, atm.Cassettes.Single(item => item.Number == 5).NoteCount);
    }

    [Fact]
    public void LoadingCreatesReproducibleMovement()
    {
        var atm = ConfiguredAtm();
        atm.Load([new(1, Currency.TRY, 100, 10)], Now);
        var cassette = atm.Cassettes.Single(item => item.Number == 1);
        var movement = Assert.Single(cassette.Movements);
        Assert.Equal(MovementType.LOAD, movement.Type);
        Assert.Equal(cassette.NoteCount, movement.NoteDelta);
        Assert.Equal(0, movement.ReservedDelta);
        Assert.Equal(Now, movement.OccurredAt);
    }

    [Fact]
    public void RecyclingCassetteRejectsMismatchedNotes()
    {
        var atm = ConfiguredAtm();
        Assert.Equal("cassette_note_mismatch", Assert.Throws<DomainException>(() => atm.Load([new(1, Currency.EUR, 100, 1)], Now)).Code);
    }

    [Fact]
    public void InServiceAtmRejectsManualLoadingAndConfiguration()
    {
        var atm = ConfiguredAtm();
        atm.Load([new(1, Currency.TRY, 100, 1)], Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        Assert.Throws<DomainException>(() => atm.Load([new(1, Currency.TRY, 100, 1)], Now));
        Assert.Throws<DomainException>(() => atm.Configure(ValidConfiguration));
    }

    [Fact]
    public void NonEmptyCassetteCannotChangeCurrency()
    {
        var atm = ConfiguredAtm();
        atm.Load([new(1, Currency.TRY, 100, 1)], Now);
        atm.ChangeState(AtmState.IN_SERVICE);
        atm.ChangeState(AtmState.MAINTENANCE);
        var configurations = ValidConfiguration;
        configurations[0] = new(1, Currency.EUR, 100);
        Assert.Equal("cassette_not_empty", Assert.Throws<DomainException>(() => atm.Configure(configurations)).Code);
    }

    private static Atm ConfiguredAtm()
    {
        var atm = Atm.Create("ATM");
        atm.Configure(ValidConfiguration);
        return atm;
    }
}
