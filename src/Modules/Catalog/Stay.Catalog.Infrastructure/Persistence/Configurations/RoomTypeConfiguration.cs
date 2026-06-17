using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class RoomTypeConfiguration : IEntityTypeConfiguration<RoomType>
{
    public void Configure(EntityTypeBuilder<RoomType> b)
    {
        b.ToTable("room_type");
        b.HasKey(r => r.Id);

        b.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(r => r.PropertyId).HasColumnName("property_id");
        b.Property(r => r.Name).HasColumnName("name");
        b.Property(r => r.UnitKind).HasColumnName("unit_kind").HasConversion(EnumConverters.UnitKind);
        b.Property(r => r.TotalUnits).HasColumnName("total_units");
        b.Property(r => r.BaseOccupancy).HasColumnName("base_occupancy");
        b.Property(r => r.MaxOccupancy).HasColumnName("max_occupancy");
        b.Property(r => r.MaxAdults).HasColumnName("max_adults");
        b.Property(r => r.MaxChildren).HasColumnName("max_children");
        b.Property(r => r.SizeSqm).HasColumnName("size_sqm").HasColumnType("numeric(6,1)");

        // Optional bed layout as jsonb; null navigation → NULL column.
        b.OwnsOne(r => r.BedConfig, x => x.ToJson("bed_config"));

        b.Property(r => r.RowVersion).HasColumnName("row_version")
            .HasDefaultValue(0).IsConcurrencyToken();

        b.HasIndex(r => r.PropertyId);
    }
}
