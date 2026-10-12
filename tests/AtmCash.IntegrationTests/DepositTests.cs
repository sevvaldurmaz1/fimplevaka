using System.Net;
using System.Net.Http.Json;
using AtmCash.Application.Deposits;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Ledger;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AtmCash.IntegrationTests.AtmScenario;

namespace AtmCash.IntegrationTests;

public sealed class DepositTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task OverflowAndReturnedNotesPersistOnlyAcceptedDepositMovements()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 98), new(5, Currency.EUR, 20, 99)]);
        var result = await DepositAsync(client, atmId, [new(Currency.TRY, 100, 5)], 2);
        Assert.Equal(3, result.AcceptedNoteCount);
        Assert.Equal(2, result.ReturnedRecognizedNoteCount);
        Assert.Equal(2, result.UnrecognizedReturnedCount);
        Assert.Equal(300, result.AcceptedTotals[Currency.TRY]);
        Assert.Equal(new[] { 1, 5 }, result.Accepted.Select(note => note.CassetteNumber));
        var inventory = await InventoryAsync(client, atmId);
        Assert.Equal(100, inventory.Cassettes.Single(cassette => cassette.Number == 1).NoteCount);
        Assert.Equal(100, inventory.Cassettes.Single(cassette => cassette.Number == 5).NoteCount);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var movements = await db.Movements.Where(movement => movement.ReferenceId == result.Id).ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.All(movements, movement =>
        {
            Assert.Equal(MovementType.DEPOSIT, movement.Type);
            Assert.Equal(0, movement.ReservedDelta);
            Assert.Equal(result.ProcessedAt, movement.OccurredAt);
        });
        Assert.Equal(3, movements.Sum(movement => movement.NoteDelta));
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task ParallelDepositsAcrossApiInstancesRespectSharedPhysicalCapacity()
    {
        using var client = fixture.Application.CreateClient();
        await using var secondApplication = fixture.Application.WithWebHostBuilder(_ => { });
        using var secondClient = secondApplication.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 98), new(5, Currency.EUR, 20, 99)]);
        using var gate = new SemaphoreSlim(0);
        async Task<DepositView> DepositTogetherAsync(HttpClient target)
        {
            await gate.WaitAsync();
            return await DepositAsync(target, atmId, [new(Currency.TRY, 100, 10)]);
        }
        var first = DepositTogetherAsync(client);
        var second = DepositTogetherAsync(secondClient);
        gate.Release(2);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(3, results.Sum(result => result.AcceptedNoteCount));
        Assert.Equal(17, results.Sum(result => result.ReturnedRecognizedNoteCount));
        var inventory = await InventoryAsync(client, atmId);
        Assert.Equal(200, inventory.Cassettes.Sum(cassette => cassette.NoteCount));
        Assert.All(inventory.Cassettes, cassette => Assert.InRange(cassette.NoteCount, 0, 100));
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task DepositPreservesExistingReservationAndOriginalQuoteCanStillBeConfirmed()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        var result = await DepositAsync(client, atmId, [new(Currency.TRY, 100, 1)]);
        Assert.Equal(1, result.AcceptedNoteCount);
        var inventory = await InventoryAsync(client, atmId);
        var stock = Assert.Single(inventory.Cassettes.Single(cassette => cassette.Number == 1).Inventory);
        Assert.Equal(3, stock.NoteCount);
        Assert.Equal(1, stock.ReservedCount);
        Assert.Equal(2, stock.AvailableCount);
        (await ConfirmAsync(client, atmId, quote.Id, 100, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        inventory = await InventoryAsync(client, atmId);
        stock = Assert.Single(inventory.Cassettes.Single(cassette => cassette.Number == 1).Inventory);
        Assert.Equal(2, stock.NoteCount);
        Assert.Equal(0, stock.ReservedCount);
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task ParallelDepositAndConfirmKeepStockAndLedgerConsistent()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 100), new(5, Currency.EUR, 20, 100)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        using var gate = new SemaphoreSlim(0);
        async Task<DepositView> DepositTogetherAsync()
        {
            await gate.WaitAsync();
            return await DepositAsync(client, atmId, [new(Currency.TRY, 100, 1)]);
        }
        async Task ConfirmTogetherAsync()
        {
            await gate.WaitAsync();
            using var response = await ConfirmAsync(client, atmId, quote.Id, 100, Guid.NewGuid().ToString());
            response.EnsureSuccessStatusCode();
        }
        var deposit = DepositTogetherAsync();
        var confirm = ConfirmTogetherAsync();
        gate.Release(2);
        await Task.WhenAll(deposit, confirm);
        var result = await deposit;
        Assert.Equal(1, result.AcceptedNoteCount + result.ReturnedRecognizedNoteCount);
        var inventory = await InventoryAsync(client, atmId);
        Assert.Equal(199 + result.AcceptedNoteCount, inventory.Cassettes.Sum(cassette => cassette.NoteCount));
        Assert.Equal(0, inventory.Cassettes.SelectMany(cassette => cassette.Inventory).Sum(note => note.ReservedCount));
        Assert.All(inventory.Cassettes, cassette => Assert.InRange(cassette.NoteCount, 0, 100));
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task InvalidLaterNoteLeavesDatabaseUnchanged()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        using var response = await RequestDepositAsync(client, atmId, [new(Currency.TRY, 100, 1), new(Currency.EUR, 10, 1)]);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "unsupported_denomination");
        Assert.Equal(1, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var ids = await db.Cassettes.Where(cassette => cassette.AtmId == atmId).Select(cassette => cassette.Id).ToArrayAsync();
        Assert.False(await db.Movements.AnyAsync(movement => ids.Contains(movement.CassetteId) && movement.Type == MovementType.DEPOSIT));
    }

    [Fact]
    public async Task MissingCountsCurrencyAndNullNotesProduceMeaningfulErrors()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        using var noUnrecognizedCount = await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits",
            new { notes = new[] { new NoteBatch(Currency.TRY, 100, 1) } }, Json);
        await AssertProblemAsync(noUnrecognizedCount, HttpStatusCode.BadRequest, "validation_failed");
        using var missingCurrency = await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { denomination = 100, count = 1 } }, unrecognizedCount = 0 });
        await AssertProblemAsync(missingCurrency, HttpStatusCode.BadRequest, "validation_failed");
        using var nullNote = await client.PostAsJsonAsync($"/api/atms/{atmId}/deposits",
            new { notes = new object?[] { null }, unrecognizedCount = 0 });
        await AssertProblemAsync(nullNote, HttpStatusCode.UnprocessableEntity, "invalid_deposit_notes");
        using var invalidCount = await RequestDepositAsync(client, atmId, [new(Currency.TRY, 100, 0)]);
        await AssertProblemAsync(invalidCount, HttpStatusCode.BadRequest, "validation_failed");
        using var negativeUnknown = await RequestDepositAsync(client, atmId, [], -1);
        await AssertProblemAsync(negativeUnknown, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task OnlyUnrecognizedNotesAndFullCapacityReturnWithoutCashMovement()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 100), new(5, Currency.EUR, 20, 100)]);
        var unknown = await DepositAsync(client, atmId, [], 3);
        Assert.Equal(0, unknown.AcceptedNoteCount);
        Assert.Empty(unknown.AcceptedTotals);
        Assert.Equal(3, unknown.UnrecognizedReturnedCount);
        var full = await DepositAsync(client, atmId, [new(Currency.TRY, 100, 2)], 1);
        Assert.Equal(0, full.AcceptedNoteCount);
        Assert.Equal(2, full.ReturnedRecognizedNoteCount);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        Assert.False(await db.Movements.AnyAsync(movement => movement.ReferenceId == unknown.Id || movement.ReferenceId == full.Id));
        Assert.Equal(200, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
    }

    [Fact]
    public async Task LargeDuplicateCountsReturnAccurateLongTotals()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 99), new(5, Currency.EUR, 20, 100)]);
        var result = await DepositAsync(client, atmId, [new(Currency.TRY, 100, int.MaxValue), new(Currency.TRY, 100, int.MaxValue)]);
        Assert.Equal(1, result.AcceptedNoteCount);
        Assert.Equal((long)int.MaxValue * 2 - 1, result.ReturnedRecognizedNoteCount);
        Assert.Equal(result.ReturnedRecognizedNoteCount, Assert.Single(result.Returned).Count);
        Assert.Equal(200, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
    }

    [Fact]
    public async Task MixedCurrencyDepositUsesAllMatchingCassettesAndReturnsSeparateCurrencyTotals()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 99), new(2, Currency.TRY, 100, 98)],
            [new(1, Currency.TRY, 100), new(2, Currency.TRY, 100), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)]);
        var result = await DepositAsync(client, atmId,
            [new(Currency.TRY, 100, 5), new(Currency.EUR, 500, 2), new(Currency.USD, 50, 1)], 1);
        Assert.Equal(8, result.AcceptedNoteCount);
        Assert.Empty(result.Returned);
        Assert.Equal(500, result.AcceptedTotals[Currency.TRY]);
        Assert.Equal(1000, result.AcceptedTotals[Currency.EUR]);
        Assert.Equal(50, result.AcceptedTotals[Currency.USD]);
        Assert.Equal(new[] { 1, 2, 5 }, result.Accepted.Where(note => note.Currency == Currency.TRY).Select(note => note.CassetteNumber));
        Assert.Equal(205, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
        using var quote = await RequestQuoteAsync(client, atmId, NewCard(), 50, Currency.USD);
        await AssertProblemAsync(quote, HttpStatusCode.UnprocessableEntity, "cash_unavailable");
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task MissingAtmAndMaintenanceRejectDepositsAndOpenApiDocumentsEndpoint()
    {
        using var client = fixture.Application.CreateClient();
        using var missing = await RequestDepositAsync(client, Guid.NewGuid(), [new(Currency.TRY, 100, 1)]);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "atm_not_found");
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        (await client.PutAsJsonAsync($"/api/atms/{atmId}/state", new { state = "MAINTENANCE" })).EnsureSuccessStatusCode();
        using var maintenance = await RequestDepositAsync(client, atmId, [new(Currency.TRY, 100, 1)]);
        await AssertProblemAsync(maintenance, HttpStatusCode.Conflict, "invalid_atm_state");
        var spec = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        Assert.True(spec.GetProperty("paths").TryGetProperty("/api/atms/{atmId}/deposits", out _));
    }

    private Task AssertLedgerMatchesAsync(Guid atmId)
        => LedgerAssertions.MatchInventoryAsync(fixture.Application.Services, atmId);
}
