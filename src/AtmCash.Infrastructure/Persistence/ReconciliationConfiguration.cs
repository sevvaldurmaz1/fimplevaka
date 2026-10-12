using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class ReconciliationConfiguration : IEntityTypeConfiguration<CashReconciliation>
{
    public void Configure(EntityTypeBuilder<CashReconciliation> builder)
    {
        builder.ToTable("Reconciliations");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.SubmittedBy).HasMaxLength(100);
        builder.Property(item => item.ApprovedBy).HasMaxLength(100);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.TotalDifferenceTry).HasPrecision(18, 6);
        builder.HasOne<Atm>().WithMany().HasForeignKey(item => item.AtmId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(item => item.Lines).WithOne().HasForeignKey(line => line.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(item => item.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class ReconciliationLineConfiguration : IEntityTypeConfiguration<ReconciliationLine>
{
    public void Configure(EntityTypeBuilder<ReconciliationLine> builder)
    {
        builder.ToTable("ReconciliationLines", table => table.HasCheckConstraint("CK_Reconciliation_Counts", "\"SystemCount\" BETWEEN 0 AND 100 AND \"CountedCount\" BETWEEN 0 AND 100"));
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();
        builder.Property(line => line.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(line => line.TryRate).HasPrecision(18, 6);
        builder.HasIndex(line => new { line.ReconciliationId, line.CassetteId, line.Currency, line.Denomination }).IsUnique();
        builder.HasOne<Cassette>().WithMany().HasForeignKey(line => line.CassetteId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(line => line.Difference);
        builder.Ignore(line => line.AbsoluteDifferenceTry);
    }
}

public sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("ExchangeRates", table => table.HasCheckConstraint("CK_ExchangeRates_Value", "\"TryRate\" > 0 AND (\"Currency\" <> 'TRY' OR \"TryRate\" = 1)"));
        builder.HasKey(rate => rate.Currency);
        builder.Property(rate => rate.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(rate => rate.Currency).ValueGeneratedNever();
        builder.Property(rate => rate.TryRate).HasPrecision(18, 6);
        builder.HasData(new ExchangeRate(Currency.TRY, 1m), new ExchangeRate(Currency.USD, 35m), new ExchangeRate(Currency.EUR, 40m));
    }
}
