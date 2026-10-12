using AtmCash.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class MovementConfiguration : IEntityTypeConfiguration<CassetteMovement>
{
    public void Configure(EntityTypeBuilder<CassetteMovement> builder)
    {
        builder.ToTable("Movements");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(item => item.Type).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(item => new { item.CassetteId, item.OccurredAt });
    }
}
