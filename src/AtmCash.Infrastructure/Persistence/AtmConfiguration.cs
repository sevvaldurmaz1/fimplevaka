using AtmCash.Domain.Atms;
using AtmCash.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class AtmConfiguration : IEntityTypeConfiguration<Atm>
{
    public void Configure(EntityTypeBuilder<Atm> builder)
    {
        builder.ToTable("Atms");
        builder.HasKey(atm => atm.Id);
        builder.Property(atm => atm.Id).ValueGeneratedNever();
        builder.Property(atm => atm.Name).HasMaxLength(100);
        builder.Property(atm => atm.State).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<CashReconciliation>().WithMany().HasForeignKey(atm => atm.PendingReconciliationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(atm => atm.Cassettes).WithOne().HasForeignKey(cassette => cassette.AtmId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(atm => atm.Cassettes).HasField("_cassettes").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
