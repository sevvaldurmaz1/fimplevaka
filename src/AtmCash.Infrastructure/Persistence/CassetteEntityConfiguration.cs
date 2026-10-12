using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class CassetteEntityConfiguration : IEntityTypeConfiguration<Cassette>
{
    public void Configure(EntityTypeBuilder<Cassette> builder)
    {
        builder.ToTable("Cassettes", table =>
        {
            table.HasCheckConstraint("CK_Cassettes_Number", "\"Number\" BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_Cassettes_TypeSlot",
                "(\"Number\" = 5 AND \"Type\" = 'DepositOnly' AND \"Currency\" IS NULL AND \"Denomination\" IS NULL) OR (\"Number\" < 5 AND \"Type\" = 'Recycling')");
            table.HasCheckConstraint("CK_Cassettes_Configuration",
                "(\"Currency\" IS NULL AND \"Denomination\" IS NULL) OR (\"Currency\" IS NOT NULL AND \"Denomination\" IS NOT NULL AND \"Currency\" IN ('TRY', 'USD', 'EUR') AND \"Denomination\" IN (20, 50, 100, 500))");
        });
        builder.HasKey(cassette => cassette.Id);
        builder.Property(cassette => cassette.Id).ValueGeneratedNever();
        builder.HasIndex(cassette => new { cassette.AtmId, cassette.Number }).IsUnique();
        builder.Property(cassette => cassette.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(cassette => cassette.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Ignore(cassette => cassette.NoteCount);
        builder.Ignore(cassette => cassette.ReservedCount);
        builder.HasMany(cassette => cassette.Inventory).WithOne().HasForeignKey(item => item.CassetteId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(cassette => cassette.Movements).WithOne().HasForeignKey(item => item.CassetteId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(cassette => cassette.Inventory).HasField("_inventory").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(cassette => cassette.Movements).HasField("_movements").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
