using AtmCash.Domain.Atms;
using AtmCash.Domain.Ledger;
using AtmCash.Domain.Withdrawals;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliation;
using AtmCash.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class CashDbContext(DbContextOptions<CashDbContext> options) : DbContext(options)
{
    public DbSet<Atm> Atms => Set<Atm>();
    public DbSet<Cassette> Cassettes => Set<Cassette>();
    public DbSet<CassetteInventory> Inventory => Set<CassetteInventory>();
    public DbSet<CassetteMovement> Movements => Set<CassetteMovement>();
    public DbSet<WithdrawalQuote> Quotes => Set<WithdrawalQuote>();
    public DbSet<WithdrawalConfirmation> Confirmations => Set<WithdrawalConfirmation>();
    public DbSet<CashReconciliation> Reconciliations => Set<CashReconciliation>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public void CaptureLowCashAlerts(Atm atm, DateTimeOffset now)
    {
        foreach (var alert in atm.RefreshLowCashAlerts(now)) Outbox.Add(new OutboxMessage(alert));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(CashDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureMovementImmutability();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureMovementImmutability();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureMovementImmutability()
    {
        if (ChangeTracker.Entries<CassetteMovement>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Movement records are immutable.");
    }
}
