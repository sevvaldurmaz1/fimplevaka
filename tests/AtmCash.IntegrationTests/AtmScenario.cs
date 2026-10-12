using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtmCash.Application.Atms;
using AtmCash.Application.Deposits;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;

namespace AtmCash.IntegrationTests;

internal static class AtmScenario
{
    private static long _cardSequence;
    public static string NewCard() => "4" + Interlocked.Increment(ref _cardSequence).ToString("D15");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<Guid> CreateAtmAsync(HttpClient client, CashLoad[] loads, CassetteConfiguration[]? configurations = null)
    {
        var create = await client.PostAsJsonAsync("/api/atms", new { name = "Integration test ATM" });
        create.EnsureSuccessStatusCode();
        var atm = (await create.Content.ReadFromJsonAsync<AtmView>(Json))!;
        configurations ??= [new(1, Currency.TRY, 100), new(2, Currency.TRY, 50), new(3, Currency.TRY, 20), new(4, Currency.USD, 100)];
        (await client.PutAsJsonAsync($"/api/atms/{atm.Id}/configuration", new { cassettes = configurations }, Json)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/atms/{atm.Id}/loads", new { notes = loads }, Json)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/atms/{atm.Id}/state", new { state = "IN_SERVICE" })).EnsureSuccessStatusCode();
        return atm.Id;
    }

    public static Task<HttpResponseMessage> RequestQuoteAsync(HttpClient client, Guid atmId, string card,
        int amount, Currency currency = Currency.TRY)
        => client.PostAsJsonAsync($"/api/atms/{atmId}/withdrawals/quotes", new { cardNumber = card, currency, amount }, Json);

    public static async Task<QuoteView> QuoteAsync(HttpClient client, Guid atmId, string card, int amount, Currency currency = Currency.TRY)
    {
        using var response = await RequestQuoteAsync(client, atmId, card, amount, currency);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QuoteView>(Json))!;
    }

    public static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid atmId, Guid quoteId, int amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes/{quoteId}/confirm")
        {
            Content = JsonContent.Create(new { amount })
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    public static async Task<AtmView> InventoryAsync(HttpClient client, Guid id)
        => (await client.GetFromJsonAsync<AtmView>($"/api/atms/{id}", Json))!;

    public static Task<HttpResponseMessage> RequestDepositAsync(HttpClient client, Guid atmId, NoteBatch[] notes, int unrecognizedCount = 0)
        => client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new { notes, unrecognizedCount }, Json);

    public static async Task<DepositView> DepositAsync(HttpClient client, Guid atmId, NoteBatch[] notes, int unrecognizedCount = 0)
    {
        using var response = await RequestDepositAsync(client, atmId, notes, unrecognizedCount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DepositView>(Json))!;
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
