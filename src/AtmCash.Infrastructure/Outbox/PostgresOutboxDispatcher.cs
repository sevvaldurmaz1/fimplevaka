using AtmCash.Application.Alerts;
using AtmCash.Application.Common;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AtmCash.Infrastructure.Outbox;

public sealed class PostgresOutboxDispatcher(CashDbContext db, TimeProvider clock,
    ILogger<PostgresOutboxDispatcher> logger) : IOutboxDispatcher
{
    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var events = await db.Outbox.FromSqlRaw("""
            SELECT * FROM "Outbox" WHERE "SentAt" IS NULL
            ORDER BY "CreatedAt", "Id" LIMIT 100 FOR UPDATE SKIP LOCKED
            """).ToArrayAsync(cancellationToken);
        foreach (var message in events)
        {
            logger.LogInformation("Outbox {EventId}: {EventType}, ATM {AtmId}, cassette {CassetteNumber}, {Currency} {Denomination}, available {AvailableCount}/100",
                message.Id, message.EventType, message.AtmId, message.CassetteNumber, message.Currency, message.Denomination, message.AvailableCount);
            message.MarkSent(clock.GetOperationTime());
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return events.Length;
    }
}
