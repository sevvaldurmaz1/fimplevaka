using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

internal static class LedgerAssertions
{
    public static async Task MatchInventoryAsync(IServiceProvider services, Guid atmId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
        var ids = await db.Cassettes.Where(cassette => cassette.AtmId == atmId).Select(cassette => cassette.Id).ToArrayAsync();
        var inventory = await db.Inventory.Where(note => ids.Contains(note.CassetteId)).ToArrayAsync();
        var movements = await db.Movements.Where(movement => ids.Contains(movement.CassetteId)).ToArrayAsync();
        var stored = inventory.ToDictionary(note => (note.CassetteId, note.Currency, note.Denomination),
            note => (NoteCount: (long)note.NoteCount, ReservedCount: (long)note.ReservedCount));
        var replayed = movements.GroupBy(movement => (movement.CassetteId, movement.Currency, movement.Denomination))
            .ToDictionary(group => group.Key, group =>
                (NoteCount: group.Sum(movement => (long)movement.NoteDelta), ReservedCount: group.Sum(movement => (long)movement.ReservedDelta)));
        foreach (var key in stored.Keys.Union(replayed.Keys))
            Assert.Equal(replayed.GetValueOrDefault(key), stored.GetValueOrDefault(key));
    }
}
