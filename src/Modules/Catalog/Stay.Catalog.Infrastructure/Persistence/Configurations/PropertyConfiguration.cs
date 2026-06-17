using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> b)
    {
        b.ToTable("property");
        b.HasKey(p => p.Id);

        b.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(p => p.HostId).HasColumnName("host_id");
        b.Property(p => p.Name).HasColumnName("name");

        b.Property(p => p.Type).HasColumnName("property_type").HasConversion(EnumConverters.PropertyType);

        b.Property(p => p.Description).HasColumnName("description");
        b.Property(p => p.StarRating).HasColumnName("star_rating");

        b.Property(p => p.Status).HasColumnName("status").HasConversion(EnumConverters.PropertyStatus);

        b.Property(p => p.Geo).HasColumnName("geo").HasColumnType("geography(Point,4326)");
        b.Property(p => p.CountryCode).HasColumnName("country_code").HasColumnType("char(2)");
        b.Property(p => p.CityId).HasColumnName("city_id");

        // Address is a value object persisted as jsonb on the address column.
        b.OwnsOne(p => p.Address, a => a.ToJson("address"));
        b.Navigation(p => p.Address).IsRequired();

        b.Property(p => p.DefaultCurrency).HasColumnName("default_currency").HasColumnType("char(3)");
        b.Property(p => p.Timezone).HasColumnName("timezone");
        b.Property(p => p.CheckInTime).HasColumnName("check_in_time");
        b.Property(p => p.CheckOutTime).HasColumnName("check_out_time");

        b.Property(p => p.CreatedAt).HasColumnName("created_at")
            .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        b.Property(p => p.UpdatedAt).HasColumnName("updated_at")
            .HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        b.Property(p => p.RowVersion).HasColumnName("row_version")
            .HasDefaultValue(0).IsConcurrencyToken();
    }
}
