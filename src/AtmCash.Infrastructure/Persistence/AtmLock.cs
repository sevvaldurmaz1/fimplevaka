using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

internal static class AtmLock
{
    // The caller must start a transaction before acquiring this row lock.
    public static async Task<Atm> LoadAsync(CashDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var atm = await db.Atms.FromSqlInterpolated($"SELECT * FROM \"Atms\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw new AtmNotFoundException(id);
        await db.Entry(atm).Collection(item => item.Cassettes).Query().Include(item => item.Inventory)
            .LoadAsync(cancellationToken);
        return atm;
    }
}
