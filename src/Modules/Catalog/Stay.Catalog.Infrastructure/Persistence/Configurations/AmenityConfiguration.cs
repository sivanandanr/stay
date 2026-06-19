using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class AmenityConfiguration : IEntityTypeConfiguration<Amenity>
{
    public void Configure(EntityTypeBuilder<Amenity> b)
    {
        b.ToTable("amenity");
        b.HasKey(a => a.Id);

        b.Property(a => a.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(a => a.Code).HasColumnName("code");
        b.Property(a => a.Category).HasColumnName("category");
        b.Property(a => a.Label).HasColumnName("label");

        b.HasIndex(a => a.Code).IsUnique();
    }
}
