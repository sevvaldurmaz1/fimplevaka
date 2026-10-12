using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AtmCash.Infrastructure.Persistence;

public sealed class CashDbContextFactory : IDesignTimeDbContextFactory<CashDbContext>
{
    public CashDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__CashDatabase")
            ?? "Host=localhost;Port=54329;Database=atm_cash;Username=atm;Password=atm_local_only";
        return new CashDbContext(new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(connection).Options);
    }
}
