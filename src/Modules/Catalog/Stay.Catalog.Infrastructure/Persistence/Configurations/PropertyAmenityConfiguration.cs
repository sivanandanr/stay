using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class PropertyAmenityConfiguration : IEntityTypeConfiguration<PropertyAmenity>
{
    public void Configure(EntityTypeBuilder<PropertyAmenity> b)
    {
        b.ToTable("property_amenity");
        b.HasKey(x => new { x.PropertyId, x.AmenityId });

        b.Property(x => x.PropertyId).HasColumnName("property_id");
        b.Property(x => x.AmenityId).HasColumnName("amenity_id");
    }
}
