using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Geo;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class CityConfiguration : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> b)
    {
        // Read-only subset of catalog.city (the geo/timezone/etc. columns are unused here).
        b.ToTable("city");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(c => c.Name).HasColumnName("name");
    }
}
