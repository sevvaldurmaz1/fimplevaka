using AtmCash.Domain.Atms;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class QuoteOptionConfiguration : IEntityTypeConfiguration<QuoteOption>
{
    public void Configure(EntityTypeBuilder<QuoteOption> builder)
    {
        builder.ToTable("QuoteOptions");
        builder.HasKey(option => option.Id);
        builder.Property(option => option.Id).ValueGeneratedNever();
        builder.Property(option => option.Kind).HasConversion<string>().HasMaxLength(5);
        builder.HasIndex(option => new { option.QuoteId, option.Amount }).IsUnique();
        builder.HasMany(option => option.Allocations).WithOne().HasForeignKey(allocation => allocation.OptionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(option => option.Allocations).HasField("_allocations").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class QuoteNoteAllocationConfiguration : IEntityTypeConfiguration<QuoteNoteAllocation>
{
    public void Configure(EntityTypeBuilder<QuoteNoteAllocation> builder)
    {
        builder.ToTable("QuoteNoteAllocations", table =>
            table.HasCheckConstraint("CK_QuoteNotes_Count", "\"Count\" BETWEEN 1 AND 100"));
        builder.HasKey(allocation => new { allocation.OptionId, allocation.CassetteId });
        builder.HasOne<Cassette>().WithMany().HasForeignKey(allocation => allocation.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}
