using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class ConfirmationConfiguration : IEntityTypeConfiguration<WithdrawalConfirmation>
{
    public void Configure(EntityTypeBuilder<WithdrawalConfirmation> builder)
    {
        builder.ToTable("Confirmations");
        builder.HasKey(item => item.Key);
        builder.Property(item => item.Key).HasMaxLength(128);
        builder.HasIndex(item => item.QuoteId).IsUnique();
        builder.HasOne<WithdrawalQuote>().WithMany().HasForeignKey(item => item.QuoteId).OnDelete(DeleteBehavior.Restrict);
    }
}
