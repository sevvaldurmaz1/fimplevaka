using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AtmCash.Api.Background;
using AtmCash.Application.Operations;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Ledger;
using AtmCash.Domain.Reconciliation;
using AtmCash.Infrastructure.Outbox;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static AtmCash.IntegrationTests.AtmScenario;

namespace AtmCash.IntegrationTests;

public sealed class OperationsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task SmallReconciliationAppliesAndLedgerRemainsConsistent()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 20)]);
        await MaintenanceAsync(client, atmId);
        var counts = await CountsAsync(client, atmId);
        counts[0] = new(1, [new(Currency.TRY, 100, 19)]);
        using var response = await ReconcileAsync(client, atmId, counts, "counter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ReconciliationView>(Json))!;
        Assert.Equal(ReconciliationStatus.APPLIED, result.Status);
        Assert.Equal(100m, result.TotalDifferenceTry);
        Assert.NotNull(result.AppliedAt);
        Assert.Equal(19, (await InventoryAsync(client, atmId)).Cassettes.Single(cassette => cassette.Number == 1).NoteCount);
        var consistency = await client.GetFromJsonAsync<ConsistencyView>($"/api/atms/{atmId}/consistency", Json);
        Assert.True(consistency!.IsConsistent);
        Assert.Empty(consistency.Differences);
        (await client.PutAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ForeignReconciliationFreezesOperationsUntilDifferentUserApproves()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 2)]);
        await MaintenanceAsync(client, atmId);
        var counts = await CountsAsync(client, atmId);
        counts[3] = new(4, [new(Currency.USD, 100, 1)]);
        using var response = await ReconcileAsync(client, atmId, counts, "Alice");
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ReconciliationView>(Json))!;
        Assert.Equal(ReconciliationStatus.AWAITING_APPROVAL, result.Status);
        Assert.Equal(3500m, result.TotalDifferenceTry);
        Assert.Equal(35m, result.Lines.Single(line => line.Currency == Currency.USD).TryRate);
        Assert.Equal(result.Id, (await InventoryAsync(client, atmId)).PendingReconciliationId);
        using var sameUser = await ApproveAsync(client, atmId, result.Id, "alice");
        await AssertProblemAsync(sameUser, HttpStatusCode.Conflict, "same_user_approval");
        using var service = await client.PutAsJsonAsync($"/api/atms/{atmId}/state", new { state = "IN_SERVICE" });
        await AssertProblemAsync(service, HttpStatusCode.Conflict, "reconciliation_awaiting_approval");
        using var load = await client.PostAsJsonAsync($"/api/atms/{atmId}/loads", new { notes = new[] { new CashLoad(4, Currency.USD, 100, 1) } }, Json);
        await AssertProblemAsync(load, HttpStatusCode.Conflict, "reconciliation_awaiting_approval");
        Assert.Equal(2, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
        // Changing the rate after counting must not change the approval threshold/history.
        await using (var scope = fixture.Application.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"ExchangeRates\" SET \"TryRate\" = 36 WHERE \"Currency\" = 'USD'");
        }
        using var approval = await ApproveAsync(client, atmId, result.Id, "Bob");
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var approved = (await approval.Content.ReadFromJsonAsync<ReconciliationView>(Json))!;
        Assert.Equal("Bob", approved.ApprovedBy);
        Assert.Equal(3500m, approved.TotalDifferenceTry);
        Assert.Equal(35m, approved.Lines.Single(line => line.Currency == Currency.USD).TryRate);
        Assert.Null((await InventoryAsync(client, atmId)).PendingReconciliationId);
        await LedgerAssertions.MatchInventoryAsync(fixture.Application.Services, atmId);
        await using var resetScope = fixture.Application.Services.CreateAsyncScope();
        await resetScope.ServiceProvider.GetRequiredService<CashDbContext>().Database.ExecuteSqlRawAsync("UPDATE \"ExchangeRates\" SET \"TryRate\" = 35 WHERE \"Currency\" = 'USD'");
    }

    [Fact]
    public async Task ParallelApprovalsProduceOnlyOneAdjustment()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(4, Currency.USD, 100, 2)]);
        await MaintenanceAsync(client, atmId);
        var counts = await CountsAsync(client, atmId);
        counts[3] = new(4, [new(Currency.USD, 100, 1)]);
        using var submission = await ReconcileAsync(client, atmId, counts, "counter");
        submission.EnsureSuccessStatusCode();
        var result = (await submission.Content.ReadFromJsonAsync<ReconciliationView>(Json))!;
        var responses = await Task.WhenAll(ApproveAsync(client, atmId, result.Id, "Bob"), ApproveAsync(client, atmId, result.Id, "Carol"));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
        await AssertProblemAsync(Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "reconciliation_already_applied");
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        Assert.Equal(1, await db.Movements.CountAsync(movement => movement.ReferenceId == result.Id && movement.Type == MovementType.RECONCILIATION_ADJUSTMENT));
        foreach (var item in responses) item.Dispose();
    }

    [Fact]
    public async Task ReportIncludesReservedAndAvailableCashAndForeignTryTotals()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 10), new(4, Currency.USD, 100, 2), new(5, Currency.EUR, 20, 3)]);
        await QuoteAsync(client, atmId, NewCard(), 100);
        var report = await client.GetFromJsonAsync<AtmReport>($"/api/atms/{atmId}/report", Json);
        Assert.NotNull(report);
        Assert.Equal(10400m, report.TotalInTry); // 1000 + 200*35 + 60*40
        Assert.Equal(100m, report.ReservedTotalInTry);
        Assert.Equal(10300m, report.AvailableTotalInTry);
        var recycling = report.Cassettes.Single(cassette => cassette.Number == 1);
        Assert.Equal(10, recycling.NoteCount);
        Assert.Equal(1, recycling.ReservedCount);
        Assert.Equal(9, recycling.AvailableCount);
        var network = await client.GetFromJsonAsync<OperationsReport>("/api/reports/operations", Json);
        Assert.Contains(network!.Atms, atm => atm.Id == atmId);
        Assert.Equal(network.Atms.Sum(atm => atm.TotalInTry), network.TotalInTry);
        Assert.True((await client.GetFromJsonAsync<ConsistencyView>($"/api/atms/{atmId}/consistency", Json))!.IsConsistent);
    }

    [Fact]
    public async Task ConsistencyDetectsTamperedBalanceAndMissingInventoryRow()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 10), new(4, Currency.USD, 100, 2)]);
        var inventory = await InventoryAsync(client, atmId);
        var firstId = inventory.Cassettes.Single(cassette => cassette.Number == 1).Id;
        var foreignId = inventory.Cassettes.Single(cassette => cassette.Number == 4).Id;
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Inventory\" SET \"NoteCount\" = 9, \"ReservedCount\" = 1 WHERE \"CassetteId\" = {firstId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Inventory\" WHERE \"CassetteId\" = {foreignId}");
        var consistency = (await client.GetFromJsonAsync<ConsistencyView>($"/api/atms/{atmId}/consistency", Json))!;
        Assert.False(consistency.IsConsistent);
        Assert.Equal(2, consistency.Differences.Count);
        var mismatch = consistency.Differences.Single(item => item.CassetteNumber == 1);
        Assert.Equal(9, mismatch.StoredNoteCount);
        Assert.Equal(10, mismatch.CalculatedNoteCount);
        Assert.Equal(1, mismatch.StoredReservedCount);
        Assert.Equal(0, mismatch.CalculatedReservedCount);
        Assert.Equal(2, consistency.Differences.Single(item => item.CassetteNumber == 4).CalculatedNoteCount);
    }

    [Fact]
    public async Task UnloadSupportsMixedDepositCassetteAndReconfigurationAfterEmptying()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 2), new(5, Currency.USD, 50, 2), new(5, Currency.EUR, 20, 2)]);
        await MaintenanceAsync(client, atmId);
        using var response = await client.PostAsJsonAsync($"/api/atms/{atmId}/unloads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 2), new CashLoad(5, Currency.USD, 50, 2), new CashLoad(5, Currency.EUR, 20, 2) }
        }, Json);
        response.EnsureSuccessStatusCode();
        Assert.Equal(0, (await InventoryAsync(client, atmId)).Cassettes.Sum(cassette => cassette.NoteCount));
        (await client.PutAsJsonAsync($"/api/atms/{atmId}/configuration", new
        {
            cassettes = new[] { new CassetteConfiguration(1, Currency.EUR, 100), new CassetteConfiguration(2, Currency.TRY, 50), new CassetteConfiguration(3, Currency.TRY, 20), new CassetteConfiguration(4, Currency.USD, 100) }
        }, Json)).EnsureSuccessStatusCode();
        await LedgerAssertions.MatchInventoryAsync(fixture.Application.Services, atmId);
    }

    [Fact]
    public async Task ReconciliationRequiresUserAndRejectsServiceStateOrIncompleteCounts()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 10)]);
        var counts = await CountsAsync(client, atmId);
        using var noUser = await client.PostAsJsonAsync($"/api/atms/{atmId}/reconciliations", new { cassettes = counts }, Json);
        await AssertProblemAsync(noUser, HttpStatusCode.BadRequest, "validation_failed");
        using var inService = await ReconcileAsync(client, atmId, counts, "counter");
        await AssertProblemAsync(inService, HttpStatusCode.Conflict, "invalid_atm_state");
        await MaintenanceAsync(client, atmId);
        using var incomplete = await ReconcileAsync(client, atmId, counts.Take(4).ToArray(), "counter");
        await AssertProblemAsync(incomplete, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task QuoteCreatesOutboxEventAndReleaseRearmsThreshold()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 10)]);
        var cassetteId = (await InventoryAsync(client, atmId)).Cassettes.Single(cassette => cassette.Number == 1).Id;
        var quote = await QuoteAsync(client, atmId, NewCard(), 100);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var message = Assert.Single(await db.Outbox.AsNoTracking().Where(item => item.CassetteId == cassetteId).ToArrayAsync());
        Assert.Equal(9, message.AvailableCount);
        Assert.Null(message.SentAt);
        (await client.DeleteAsync($"/api/atms/{atmId}/withdrawals/quotes/{quote.Id}")).EnsureSuccessStatusCode();
        await QuoteAsync(client, atmId, NewCard(), 100);
        Assert.Equal(2, await db.Outbox.CountAsync(item => item.CassetteId == cassetteId));
    }

    [Fact]
    public async Task OutboxWriteFailureRollsBackQuoteReservationMovementAndAlertFlag()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 10)]);
        var cassetteId = (await InventoryAsync(client, atmId)).Cassettes.Single(cassette => cassette.Number == 1).Id;
        await using var brokenApplication = fixture.Application.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<CashDbContext>(options => options.AddInterceptors(new FailOutboxWrite(cassetteId)))));
        using var brokenClient = brokenApplication.CreateClient();
        using var response = await RequestQuoteAsync(brokenClient, atmId, NewCard(), 100);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        var notes = Assert.Single((await InventoryAsync(client, atmId)).Cassettes.Single(cassette => cassette.Number == 1).Inventory);
        Assert.Equal(0, notes.ReservedCount);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        Assert.False(await db.Quotes.AnyAsync(quote => quote.AtmId == atmId));
        Assert.False(await db.Outbox.AnyAsync(message => message.CassetteId == cassetteId));
        Assert.False((await db.Cassettes.AsNoTracking().SingleAsync(cassette => cassette.Id == cassetteId)).LowCashAlertActive);
        await LedgerAssertions.MatchInventoryAsync(fixture.Application.Services, atmId);
    }

    [Fact]
    public async Task ConcurrentOutboxDispatchersClaimEachEventOnce()
    {
        using var client = fixture.Application.CreateClient();
        await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        await using var firstScope = fixture.Application.Services.CreateAsyncScope();
        await using var secondScope = fixture.Application.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<CashDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<CashDbContext>();
        var expected = await firstDb.Outbox.AsNoTracking().Where(message => message.SentAt == null).Select(message => message.Id).ToArrayAsync();
        var logs = new EventLogger();
        var counts = await Task.WhenAll(new PostgresOutboxDispatcher(firstDb, fixture.Clock, logs).DispatchAsync(default),
            new PostgresOutboxDispatcher(secondDb, fixture.Clock, logs).DispatchAsync(default));
        Assert.Equal(expected.Length, counts.Sum());
        Assert.Equal(expected.Order(), logs.Events.Order());
        Assert.False(await firstDb.Outbox.AnyAsync(message => message.SentAt == null));
    }

    [Fact]
    public async Task FailedOutboxLogLeavesEventsUnsentForRetry()
    {
        using var client = fixture.Application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var logs = new EventLogger { Fail = true };
        var dispatcher = new PostgresOutboxDispatcher(db, fixture.Clock, logs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(default));
        Assert.True(await db.Outbox.AnyAsync(message => message.AtmId == atmId && message.SentAt == null));
        logs.Fail = false;
        Assert.True(await dispatcher.DispatchAsync(default) > 0);
        Assert.False(await db.Outbox.AnyAsync(message => message.SentAt == null));
    }

    [Fact]
    public async Task HostedOutboxServiceMarksEventsSentWithoutManualDispatch()
    {
        await using var application = fixture.Application.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddHostedService<OutboxDispatcherWorker>()));
        using var client = application.CreateClient();
        var atmId = await CreateAtmAsync(client, [new(1, Currency.TRY, 100, 1)]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
            var messages = await db.Outbox.AsNoTracking().Where(message => message.AtmId == atmId).ToArrayAsync(deadline.Token);
            if (messages.Length > 0 && messages.All(message => message.SentAt is not null)) break;
            await Task.Delay(50, deadline.Token);
        }
    }

    private static Task<HttpResponseMessage> ReconcileAsync(HttpClient client, Guid atmId, CassetteCount[] counts, string user)
        => UserRequestAsync(client, $"/api/atms/{atmId}/reconciliations", user, new { cassettes = counts });
    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid atmId, Guid id, string user)
        => UserRequestAsync(client, $"/api/atms/{atmId}/reconciliations/{id}/approve", user, null);
    private static async Task<HttpResponseMessage> UserRequestAsync(HttpClient client, string path, string user, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-User", user);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
    }
    private static async Task MaintenanceAsync(HttpClient client, Guid id)
        => (await client.PutAsJsonAsync($"/api/atms/{id}/state", new { state = "MAINTENANCE" })).EnsureSuccessStatusCode();
    private static async Task<CassetteCount[]> CountsAsync(HttpClient client, Guid id)
        => (await InventoryAsync(client, id)).Cassettes.Select(cassette => new CassetteCount(cassette.Number,
            cassette.Inventory.Select(note => new NoteBatch(note.Currency, note.Denomination, note.NoteCount)).ToArray())).ToArray();

    private sealed class FailOutboxWrite(Guid cassetteId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.State == EntityState.Added && entry.Entity.CassetteId == cassetteId))
                throw new InvalidOperationException("Injected outbox write failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class EventLogger : ILogger<PostgresOutboxDispatcher>
    {
        public ConcurrentBag<Guid> Events { get; } = [];
        public bool Fail { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Fail) throw new InvalidOperationException("Injected log failure");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Events.Add((Guid)values.Single(value => value.Key == "EventId").Value!);
        }
    }
}
