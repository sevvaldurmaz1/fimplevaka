using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Outbox;

public sealed class OutboxConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("Outbox");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.EventType).HasMaxLength(20);
        builder.Property(message => message.Currency).HasConversion<string>().HasMaxLength(3);
        builder.HasIndex(message => new { message.SentAt, message.CreatedAt });
        builder.HasOne<Cassette>().WithMany().HasForeignKey(message => message.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}
