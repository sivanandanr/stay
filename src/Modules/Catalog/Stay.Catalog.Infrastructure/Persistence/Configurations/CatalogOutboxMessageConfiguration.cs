using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Application.Persistence;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class CatalogOutboxMessageConfiguration : IEntityTypeConfiguration<CatalogOutboxMessage>
{
    public void Configure(EntityTypeBuilder<CatalogOutboxMessage> b)
    {
        b.ToTable("outbox_message");
        b.HasKey(m => m.Id);

        b.Property(m => m.Id).HasColumnName("id");
        b.Property(m => m.Type).HasColumnName("type");
        b.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(m => m.OccurredAt).HasColumnName("occurred_at")
            .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        b.Property(m => m.ProcessedAt).HasColumnName("processed_at");
    }
}
