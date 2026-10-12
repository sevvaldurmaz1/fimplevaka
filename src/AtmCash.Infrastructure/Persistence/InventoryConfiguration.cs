using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class InventoryConfiguration : IEntityTypeConfiguration<CassetteInventory>
{
    public void Configure(EntityTypeBuilder<CassetteInventory> builder)
    {
        builder.ToTable("Inventory", table =>
        {
            table.HasCheckConstraint("CK_Inventory_Counts", "\"NoteCount\" BETWEEN 0 AND 100 AND \"ReservedCount\" BETWEEN 0 AND \"NoteCount\"");
            table.HasCheckConstraint("CK_Inventory_Notes", "\"Currency\" IN ('TRY', 'USD', 'EUR') AND \"Denomination\" IN (20, 50, 100, 500)");
        });
        builder.HasKey(item => new { item.CassetteId, item.Currency, item.Denomination });
        builder.Property(item => item.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Ignore(item => item.AvailableCount);
    }
}
