using System.Data;
using AtmCash.Application.Atms;
using AtmCash.Application.Common;
using AtmCash.Application.Operations;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class PostgresOperationsStore(CashDbContext db, TimeProvider clock) : IOperationsStore
{
    public async Task<T> ExecuteAsync<T>(Guid atmId, Func<IOperationsSession, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var atm = await AtmLock.LoadAsync(db, atmId, token);
        var result = await operation(new Session(db, atm), token);
        db.CaptureLowCashAlerts(atm, clock.GetOperationTime());
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    public Task<CashReconciliation?> FindReconciliationAsync(Guid atmId, Guid id, CancellationToken token)
        => db.Reconciliations.AsNoTracking().Include(item => item.Lines).SingleOrDefaultAsync(item => item.Id == id && item.AtmId == atmId, token);

    public async Task<OperationsSnapshot> ReadReportAsync(Guid? atmId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var query = Atms;
        if (atmId is not null) query = query.Where(atm => atm.Id == atmId);
        var atms = await query.OrderBy(atm => atm.Id).ToArrayAsync(token);
        if (atmId is not null && atms.Length == 0) throw new AtmNotFoundException(atmId.Value);
        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(rate => rate.Currency, rate => rate.TryRate, token);
        await transaction.CommitAsync(token);
        return new(atms, rates);
    }

    public async Task<ConsistencySnapshot> ReadConsistencyAsync(Guid atmId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var atm = await Atms.SingleOrDefaultAsync(atm => atm.Id == atmId, token) ?? throw new AtmNotFoundException(atmId);
        var balances = await db.Movements.AsNoTracking().Where(movement => db.Cassettes.Any(cassette => cassette.Id == movement.CassetteId && cassette.AtmId == atmId))
            .GroupBy(movement => new { movement.CassetteId, movement.Currency, movement.Denomination })
            .Select(group => new LedgerBalance(group.Key.CassetteId, group.Key.Currency, group.Key.Denomination,
                group.Sum(movement => (long)movement.NoteDelta), group.Sum(movement => (long)movement.ReservedDelta))).ToArrayAsync(token);
        await transaction.CommitAsync(token);
        return new(atm, balances);
    }

    private IQueryable<Atm> Atms => db.Atms.AsNoTracking().AsSingleQuery().Include(atm => atm.Cassettes).ThenInclude(cassette => cassette.Inventory);

    private sealed class Session(CashDbContext db, Atm atm) : IOperationsSession
    {
        public Atm Atm { get; } = atm;
        public async Task<IReadOnlyDictionary<Currency, decimal>> ReadRatesAsync(CancellationToken token)
            => await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(rate => rate.Currency, rate => rate.TryRate, token);
        public Task<CashReconciliation?> FindReconciliationAsync(Guid id, CancellationToken token)
            => db.Reconciliations.Include(item => item.Lines).SingleOrDefaultAsync(item => item.Id == id && item.AtmId == Atm.Id, token);
        public void AddReconciliation(CashReconciliation reconciliation) => db.Reconciliations.Add(reconciliation);
    }
}
