using AtmCash.Application.Atms;
using AtmCash.Application.Common;
using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class PostgresAtmStore(CashDbContext db, TimeProvider clock) : IAtmStore
{
    public async Task AddAsync(Atm atm, CancellationToken cancellationToken)
    {
        db.Atms.Add(atm);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Atm?> FindAsync(Guid id, CancellationToken cancellationToken)
        // Five cassettes and twelve supported note types keep this result bounded;
        // a single statement also reads a consistent inventory snapshot.
        => db.Atms.AsNoTracking().AsSingleQuery().Include(atm => atm.Cassettes).ThenInclude(cassette => cassette.Inventory)
            .SingleOrDefaultAsync(atm => atm.Id == id, cancellationToken);

    public Task<Atm> UpdateAsync(Guid id, Action<Atm> change, CancellationToken cancellationToken)
        => UpdateAsync(id, atm =>
        {
            change(atm);
            return atm;
        }, cancellationToken);

    public async Task<TResult> UpdateAsync<TResult>(Guid id, Func<Atm, TResult> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the aggregate root before loading inventory: serializes even different API instances.
        var atm = await AtmLock.LoadAsync(db, id, cancellationToken);
        var result = change(atm);
        db.CaptureLowCashAlerts(atm, clock.GetOperationTime());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
