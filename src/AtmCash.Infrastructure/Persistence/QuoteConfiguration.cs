using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence;

public sealed class QuoteConfiguration : IEntityTypeConfiguration<WithdrawalQuote>
{
    public void Configure(EntityTypeBuilder<WithdrawalQuote> builder)
    {
        builder.ToTable("Quotes", table =>
        {
            table.HasCheckConstraint("CK_Quotes_Amounts", "\"RequestedAmount\" > 0 AND \"ReservedAmount\" > 0");
            table.HasCheckConstraint("CK_Quotes_Confirmation",
                "(\"Status\" = 'CONFIRMED' AND \"ConfirmedAmount\" IS NOT NULL AND \"ConfirmedAmount\" > 0 AND \"ConfirmedAt\" IS NOT NULL AND \"ConfirmedBusinessDate\" IS NOT NULL) OR (\"Status\" <> 'CONFIRMED' AND \"ConfirmedAmount\" IS NULL AND \"ConfirmedAt\" IS NULL AND \"ConfirmedBusinessDate\" IS NULL)");
        });
        builder.HasKey(quote => quote.Id);
        builder.Property(quote => quote.Id).ValueGeneratedNever();
        builder.Property(quote => quote.CardHash).HasMaxLength(64);
        builder.Property(quote => quote.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(quote => quote.Status).HasConversion<string>().HasMaxLength(12);
        builder.HasIndex(quote => new { quote.Status, quote.ExpiresAt, quote.AtmId });
        builder.HasIndex(quote => new { quote.CardHash, quote.Currency, quote.Status, quote.ConfirmedBusinessDate });
        builder.HasOne<Domain.Atms.Atm>().WithMany().HasForeignKey(quote => quote.AtmId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(quote => quote.Options).WithOne().HasForeignKey(option => option.QuoteId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(quote => quote.Options).HasField("_options").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
