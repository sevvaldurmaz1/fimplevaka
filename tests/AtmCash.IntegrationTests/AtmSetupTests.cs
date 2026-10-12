using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AtmCash.IntegrationTests;

public sealed class AtmSetupTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SetupRoundTripPersistsInventoryAndLedgerAndEnforcesLifecycle()
    {
        using var client = fixture.Application.CreateClient();
        var id = await CreateConfiguredAsync(client);
        var load = await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 10), new CashLoad(4, Currency.USD, 100, 5) }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        Assert.Equal(AtmState.LOADED, (await load.Content.ReadFromJsonAsync<AtmView>(Json))!.State);
        var change = await client.PutAsJsonAsync($"/api/atms/{id}/state", new { state = "IN_SERVICE" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var persisted = await client.GetFromJsonAsync<AtmView>($"/api/atms/{id}", Json);
        Assert.NotNull(persisted);
        Assert.Equal(AtmState.IN_SERVICE, persisted.State);
        Assert.Equal(10, persisted.Cassettes.Single(item => item.Number == 1).NoteCount);

        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var cassetteIds = persisted.Cassettes.Select(item => item.Id).ToArray();
        var movements = await db.Movements.Where(item => cassetteIds.Contains(item.CassetteId)).ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.Equal(15, movements.Sum(item => item.NoteDelta));

        var rejected = await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 1) }
        }, Json);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_atm_state", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ParallelLoadsCannotExceedCassetteCapacity()
    {
        using var client = fixture.Application.CreateClient();
        var id = await CreateConfiguredAsync(client);
        var initial = await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 60) }
        }, Json);
        initial.EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/atms/{id}/state", new { state = "IN_SERVICE" })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/atms/{id}/state", new { state = "MAINTENANCE" })).EnsureSuccessStatusCode();

        using var gate = new SemaphoreSlim(0);
        async Task<HttpResponseMessage> LoadAsync()
        {
            await gate.WaitAsync();
            return await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
            {
                notes = new[] { new CashLoad(1, Currency.TRY, 100, 30) }
            }, Json);
        }
        var first = LoadAsync();
        var second = LoadAsync();
        gate.Release(2);
        var responses = await Task.WhenAll(first, second);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        var atm = await client.GetFromJsonAsync<AtmView>($"/api/atms/{id}", Json);
        Assert.Equal(90, atm!.Cassettes.Single(item => item.Number == 1).NoteCount);
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task FailedLoadDoesNotPersistPartialInventoryOrMovements()
    {
        using var client = fixture.Application.CreateClient();
        var id = await CreateConfiguredAsync(client);
        var response = await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 1), new CashLoad(2, Currency.TRY, 50, 101) }
        }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var atm = await client.GetFromJsonAsync<AtmView>($"/api/atms/{id}", Json);
        Assert.Equal(AtmState.CONFIGURED, atm!.State);
        Assert.All(atm.Cassettes, item => Assert.Equal(0, item.NoteCount));
        await using var scope = fixture.Application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var ids = atm.Cassettes.Select(item => item.Id).ToArray();
        Assert.Empty(await db.Movements.Where(item => ids.Contains(item.CassetteId)).ToArrayAsync());
    }

    [Theory]
    [InlineData("UPDATE \"Movements\" SET \"NoteDelta\" = 999")]
    [InlineData("DELETE FROM \"Movements\"")]
    [InlineData("TRUNCATE TABLE \"Movements\"")]
    public async Task MovementRecordsCannotBeChangedThroughDirectSql(string sql)
    {
        using var client = fixture.Application.CreateClient();
        var id = await CreateConfiguredAsync(client);
        (await client.PostAsJsonAsync($"/api/atms/{id}/loads", new
        {
            notes = new[] { new CashLoad(1, Currency.TRY, 100, 1) }
        }, Json)).EnsureSuccessStatusCode();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("P0001", error.SqlState);
    }

    [Fact]
    public async Task HealthAndOpenApiAreAvailable()
    {
        using var client = fixture.Application.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/atms", out _));
    }

    [Fact]
    public async Task InvalidInputAndMissingAtmReturnStableProblemCodes()
    {
        using var client = fixture.Application.CreateClient();
        var invalid = await client.PostAsJsonAsync("/api/atms", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal("validation_failed", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var missing = await client.GetAsync($"/api/atms/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("atm_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingStateAndNullLoadEntryReturnMeaningfulErrors()
    {
        using var client = fixture.Application.CreateClient();
        var id = await CreateConfiguredAsync(client);
        var missingState = await client.PutAsJsonAsync($"/api/atms/{id}/state", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingState.StatusCode);
        Assert.Equal("validation_failed", (await missingState.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var nullEntry = await client.PostAsJsonAsync($"/api/atms/{id}/loads", new { notes = new object?[] { null } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nullEntry.StatusCode);
        Assert.Equal("empty_cash_load", (await nullEntry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<Guid> CreateConfiguredAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/atms", new { name = "Integration ATM" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var atm = await created.Content.ReadFromJsonAsync<AtmView>(Json);
        Assert.NotNull(atm);
        var configured = await client.PutAsJsonAsync($"/api/atms/{atm.Id}/configuration", new
        {
            cassettes = new[]
            {
                new CassetteConfiguration(1, Currency.TRY, 100), new CassetteConfiguration(2, Currency.TRY, 50),
                new CassetteConfiguration(3, Currency.TRY, 20), new CassetteConfiguration(4, Currency.USD, 100)
            }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        return atm.Id;
    }
}
