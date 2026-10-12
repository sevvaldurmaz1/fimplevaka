using System.Net;
using System.Net.Http.Json;
using AtmCash.Api.Background;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Ledger;
using AtmCash.Domain.Withdrawals;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AtmCash.IntegrationTests.WithdrawalScenario;

namespace AtmCash.IntegrationTests;

public sealed class WithdrawalTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AlternativesReserveNotesAndConfirmationDebitsOnlySelectedAmount()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(2, Currency.TRY, 50, 1), new(3, Currency.TRY, 20, 2)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 80);
        Assert.Equal(new[] { 70, 90 }, quote.Options.Select(option => option.Amount));
        Assert.Equal(90, quote.ReservedAmount);
        var inventory = await InventoryAsync(client, atmId);
        Assert.Equal(3, inventory.Cassettes.Sum(item => item.NoteCount));
        Assert.Equal(3, inventory.Cassettes.SelectMany(item => item.Inventory).Sum(item => item.ReservedCount));
        using var blocked = await RequestQuoteAsync(client, atmId, NewCard(), 20);
        await AssertProblemAsync(blocked, HttpStatusCode.UnprocessableEntity, "cash_unavailable");

        using var confirmation = await ConfirmAsync(client, atmId, quote.Id, 70, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var result = (await confirmation.Content.ReadFromJsonAsync<ConfirmationView>(Json))!;
        Assert.Equal(70, result.Amount);
        inventory = await InventoryAsync(client, atmId);
        Assert.Equal(1, inventory.Cassettes.Sum(item => item.NoteCount));
        Assert.Equal(0, inventory.Cassettes.SelectMany(item => item.Inventory).Sum(item => item.ReservedCount));
        Assert.Single((await QuoteAsync(client, atmId, NewCard(), 20)).Options);
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task ParallelQuotesAcrossApiInstancesNeverReserveTheSameNoteTwice()
    {
        using var client = fixture.Application.CreateClient();
        await using var otherApplication = fixture.Application.WithWebHostBuilder(_ => { });
        using var otherClient = otherApplication.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 4)]);
        using var gate = new SemaphoreSlim(0);
        async Task<HttpResponseMessage> QuoteTogetherAsync(int index)
        {
            await gate.WaitAsync();
            return await RequestQuoteAsync(index % 2 == 0 ? client : otherClient, atmId, NewCard(), 100);
        }
        var tasks = Enumerable.Range(0, 20).Select(QuoteTogetherAsync).ToArray();
        gate.Release(tasks.Length);
        var responses = await Task.WhenAll(tasks);
        Assert.Equal(4, responses.Count(item => item.StatusCode == HttpStatusCode.Created));
        Assert.Equal(16, responses.Count(item => item.StatusCode == HttpStatusCode.UnprocessableEntity));
        var inventory = await InventoryAsync(client, atmId);
        var notes = Assert.Single(inventory.Cassettes.Single(item => item.Number == 1).Inventory);
        Assert.Equal(4, notes.NoteCount);
        Assert.Equal(4, notes.ReservedCount);
        Assert.Equal(0, notes.AvailableCount);
        await AssertLedgerMatchesAsync(atmId);
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task ConcurrentSameKeyConfirmationsReturnIdenticalResultAndDebitOnce()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 5)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(ConfirmAsync(client, atmId, quote.Id, 100, key), ConfirmAsync(client, atmId, quote.Id, 100, key));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
        Assert.Equal(4, (await InventoryAsync(client, atmId)).Cassettes.Sum(item => item.NoteCount));
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        Assert.Single(await db.Movements.Where(item => item.ReferenceId == quote.Id && item.Type == MovementType.WITHDRAWAL).ToArrayAsync());
        Assert.Equal(1, await db.Confirmations.CountAsync(item => item.Key == key));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task ReplayedConfirmationSurvivesDifferentApiInstanceAndExpiry()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 5)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        var key = Guid.NewGuid().ToString();
        // PostgreSQL timestamps have microsecond precision, whereas .NET clocks have 100ns ticks.
        fixture.Clock.Advance(TimeSpan.FromTicks(7));
        using var confirmed = await ConfirmAsync(client, atmId, quote.Id, 100, key);
        confirmed.EnsureSuccessStatusCode();
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        await using var restarted = fixture.Application.WithWebHostBuilder(_ => { });
        using var otherClient = restarted.CreateClient();
        (await client.PutAsJsonAsync($"/api/atms/{atmId}/state", new { state = "MAINTENANCE" })).EnsureSuccessStatusCode();
        using var replay = await ConfirmAsync(otherClient, atmId, quote.Id, 100, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await confirmed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var changed = await ConfirmAsync(otherClient, atmId, quote.Id, 200, key);
        await AssertProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_conflict");
        using var freshKey = await ConfirmAsync(otherClient, atmId, quote.Id, 100, Guid.NewGuid().ToString());
        await AssertProblemAsync(freshKey, HttpStatusCode.Conflict, "quote_already_confirmed");
    }

    [Fact]
    public async Task GlobalIdempotencyKeyCannotConfirmTwoDifferentQuotesConcurrently()
    {
        using var client = fixture.Application.CreateClient();
        var firstAtm = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2)]);
        var secondAtm = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2)]);
        var firstQuote = await QuoteAsync(client, firstAtm, NewCard(), 100);
        var secondQuote = await QuoteAsync(client, secondAtm, NewCard(), 100);
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(ConfirmAsync(client, firstAtm, firstQuote.Id, 100, key),
            ConfirmAsync(client, secondAtm, secondQuote.Id, 100, key));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
        await AssertProblemAsync(Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "idempotency_conflict");
        var firstInventory = await InventoryAsync(client, firstAtm);
        var secondInventory = await InventoryAsync(client, secondAtm);
        Assert.Equal(3, firstInventory.Cassettes.Concat(secondInventory.Cassettes).Sum(item => item.NoteCount));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task ParallelQuotesAtDifferentAtmsShareCardsDailyLimit()
    {
        using var client = fixture.Application.CreateClient();
        Domain.Atms.CassetteConfiguration[] configuration =
            [new(1, Currency.TRY, 500), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)];
        var firstAtm = await CreateAtmAsync(client, [new(1, Currency.TRY, 500, 30)], configuration);
        var secondAtm = await CreateAtmAsync(client, [new(1, Currency.TRY, 500, 30)], configuration);
        var card = NewCard();
        var responses = await Task.WhenAll(RequestQuoteAsync(client, firstAtm, card, 12000), RequestQuoteAsync(client, secondAtm, card, 12000));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Created);
        await AssertProblemAsync(Assert.Single(responses, item => item.StatusCode == HttpStatusCode.UnprocessableEntity),
            HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
        Assert.Equal(24, (await InventoryAsync(client, firstAtm)).Cassettes.Concat((await InventoryAsync(client, secondAtm)).Cassettes)
            .SelectMany(item => item.Inventory).Sum(item => item.ReservedCount));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task ReservedAndConfirmedAmountsCountTowardsIndependentCurrencyLimits()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client,
            [new(1, Currency.TRY, 500, 40), new(2, Currency.TRY, 20, 1), new(3, Currency.USD, 100, 10), new(4, Currency.EUR, 100, 10)],
            [new(1, Currency.TRY, 500), new(2, Currency.TRY, 20), new(3, Currency.USD, 100), new(4, Currency.EUR, 100)]);
        var card = NewCard();
        var quote = await QuoteAsync(client, atmId, card, 20000);
        using var reservedLimit = await RequestQuoteAsync(client, atmId, card, 20);
        await AssertProblemAsync(reservedLimit, HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
        Assert.Equal(1000, (await QuoteAsync(client, atmId, card, 1000, Currency.USD)).ReservedAmount);
        Assert.Equal(1000, (await QuoteAsync(client, atmId, card, 1000, Currency.EUR)).ReservedAmount);
        (await ConfirmAsync(client, atmId, quote.Id, 20000, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        using var confirmedLimit = await RequestQuoteAsync(client, atmId, card, 20);
        await AssertProblemAsync(confirmedLimit, HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
    }

    [Fact]
    public async Task UpperAlternativeIsSuppressedWhenItExceedsRemainingDailyLimit()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(3, Currency.USD, 50, 19), new(4, Currency.USD, 20, 10)],
            [new(1, Currency.TRY, 100), new(2, Currency.TRY, 20), new(3, Currency.USD, 50), new(4, Currency.USD, 20)]);
        var card = NewCard();
        var earlier = await QuoteAsync(client, atmId, card, 950, Currency.USD);
        (await ConfirmAsync(client, atmId, earlier.Id, 950, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        var quote = await QuoteAsync(client, atmId, card, 45, Currency.USD);
        Assert.Equal(40, Assert.Single(quote.Options).Amount);
        Assert.Equal(QuoteOptionKind.Lower, Assert.Single(quote.Options).Kind);
        (await ConfirmAsync(client, atmId, quote.Id, 40, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        using var exhausted = await RequestQuoteAsync(client, atmId, card, 1, Currency.USD);
        await AssertProblemAsync(exhausted, HttpStatusCode.UnprocessableEntity, "cash_unavailable");
    }

    [Fact]
    public async Task CancelIsRepeatableAndRestoresStockAndDailyLimit()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 10)]);
        var card = NewCard();
        var quote = await QuoteAsync(client, atmId, card, 1000, Currency.USD);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var cancelled = await client.DeleteAsync($"/api/atms/{atmId}/withdrawals/quotes/{quote.Id}");
            Assert.Equal(QuoteStatus.CANCELLED, (await cancelled.Content.ReadFromJsonAsync<QuoteView>(Json))!.Status);
        }
        using var confirm = await ConfirmAsync(client, atmId, quote.Id, 1000, Guid.NewGuid().ToString());
        await AssertProblemAsync(confirm, HttpStatusCode.Conflict, "quote_cancelled");
        Assert.Equal(10, (await InventoryAsync(client, atmId)).Cassettes.Sum(item => item.NoteCount));
        Assert.Equal(1000, (await QuoteAsync(client, atmId, card, 1000, Currency.USD)).ReservedAmount);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        Assert.Equal(1, await db.Movements.CountAsync(item => item.ReferenceId == quote.Id && item.Type == MovementType.RESERVATION_RELEASE));
    }

    [Fact]
    public async Task ExpiredConfirmCommitsReleaseBeforeReturningGone()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 10)]);
        var card = NewCard();
        var quote = await QuoteAsync(client, atmId, card, 1000, Currency.USD);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        using var confirm = await ConfirmAsync(client, atmId, quote.Id, 1000, Guid.NewGuid().ToString());
        await AssertProblemAsync(confirm, HttpStatusCode.Gone, "quote_expired");
        var inventory = await InventoryAsync(client, atmId);
        Assert.Equal(10, inventory.Cassettes.Sum(item => item.NoteCount));
        Assert.Equal(0, inventory.Cassettes.SelectMany(item => item.Inventory).Sum(item => item.ReservedCount));
        Assert.Equal(1000, (await QuoteAsync(client, atmId, card, 1000, Currency.USD)).ReservedAmount);
        await AssertLedgerMatchesAsync(atmId);
    }

    [Fact]
    public async Task HostedWorkerAutomaticallyReleasesExpiredQuotesWithoutCustomerRequest()
    {
        await using var application = fixture.Application.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddHostedService<ReservationExpirationWorker>()));
        using var client = application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
            var status = await db.Quotes.AsNoTracking().Where(item => item.Id == quote.Id).Select(item => item.Status).SingleAsync(deadline.Token);
            if (status == QuoteStatus.EXPIRED)
            {
                var cassette = quote.Options.Single().Cassettes.Single().CassetteId;
                Assert.Equal(0, await db.Inventory.Where(item => item.CassetteId == cassette).SumAsync(item => item.ReservedCount, deadline.Token));
                break;
            }
            await Task.Delay(50, deadline.Token);
        }
    }

    [Fact]
    public async Task ConfirmationAfterIstanbulMidnightUsesNewDaysLimit()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 20)]);
        AdvanceToBeforeMidnight();
        var card = NewCard();
        var quote = await QuoteAsync(client, atmId, card, 1000, Currency.USD);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        (await ConfirmAsync(client, atmId, quote.Id, 1000, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        using var overLimit = await RequestQuoteAsync(client, atmId, card, 100, Currency.USD);
        await AssertProblemAsync(overLimit, HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var persisted = await db.Quotes.AsNoTracking().SingleAsync(item => item.Id == quote.Id);
        Assert.Equal(DateOnly.FromDateTime(fixture.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(3)).DateTime), persisted.ConfirmedBusinessDate);
    }

    [Fact]
    public async Task DailyLimitResetsAtIstanbulMidnight()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 20)]);
        AdvanceToBeforeMidnight();
        var card = NewCard();
        var first = await QuoteAsync(client, atmId, card, 1000, Currency.USD);
        (await ConfirmAsync(client, atmId, first.Id, 1000, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var second = await QuoteAsync(client, atmId, card, 1000, Currency.USD);
        (await ConfirmAsync(client, atmId, second.Id, 1000, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        Assert.Equal(0, (await InventoryAsync(client, atmId)).Cassettes.Sum(item => item.NoteCount));
    }

    [Fact]
    public async Task CardPanIsAbsentFromPersistenceAndResponses()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2)]);
        var card = NewCard();
        using var response = await RequestQuoteAsync(client, atmId, card, 100);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(card, body);
        Assert.DoesNotContain("cardHash", body);
        var quote = (await response.Content.ReadFromJsonAsync<QuoteView>(Json))!;
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var stored = await db.Quotes.AsNoTracking().SingleAsync(item => item.Id == quote.Id);
        Assert.Equal(64, stored.CardHash.Length);
        Assert.DoesNotContain(card, stored.CardHash);
        Assert.DoesNotContain(db.Model.GetEntityTypes().SelectMany(type => type.GetProperties()),
            property => property.Name.Contains("CardNumber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfirmRequiresHeaderAndOfferedAmountAndReservationBlocksMaintenance()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 3)]);
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        using var noKey = await client.PostAsJsonAsync($"/api/atms/{atmId}/withdrawals/quotes/{quote.Id}/confirm", new { amount = 100 });
        await AssertProblemAsync(noKey, HttpStatusCode.BadRequest, "validation_failed");
        using var wrongAmount = await ConfirmAsync(client, atmId, quote.Id, 200, Guid.NewGuid().ToString());
        await AssertProblemAsync(wrongAmount, HttpStatusCode.UnprocessableEntity, "amount_not_offered");
        using var maintenance = await client.PutAsJsonAsync($"/api/atms/{atmId}/state", new { state = "MAINTENANCE" });
        await AssertProblemAsync(maintenance, HttpStatusCode.Conflict, "invalid_state_transition");
        Assert.Equal(1, (await InventoryAsync(client, atmId)).Cassettes.SelectMany(item => item.Inventory).Sum(item => item.ReservedCount));
    }

    private void AdvanceToBeforeMidnight()
    {
        var now = fixture.Clock.GetUtcNow();
        var local = now.ToOffset(TimeSpan.FromHours(3));
        var target = new DateTimeOffset(local.Date.AddDays(1).AddSeconds(-30), TimeSpan.FromHours(3));
        fixture.Clock.Advance(target.ToUniversalTime() - now);
    }

    private async Task AssertLedgerMatchesAsync(Guid atmId)
    {
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var cassetteIds = await db.Cassettes.Where(item => item.AtmId == atmId).Select(item => item.Id).ToArrayAsync();
        var inventory = await db.Inventory.Where(item => cassetteIds.Contains(item.CassetteId)).ToArrayAsync();
        var movements = await db.Movements.Where(item => cassetteIds.Contains(item.CassetteId)).ToArrayAsync();
        foreach (var item in inventory)
        {
            var history = movements.Where(movement => movement.CassetteId == item.CassetteId
                && movement.Currency == item.Currency && movement.Denomination == item.Denomination).ToArray();
            Assert.Equal(item.NoteCount, history.Sum(movement => movement.NoteDelta));
            Assert.Equal(item.ReservedCount, history.Sum(movement => movement.ReservedDelta));
        }
    }
}
