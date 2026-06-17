using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Catalog.Domain.Hosts;

namespace Stay.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class HostConfiguration : IEntityTypeConfiguration<Host>
{
    public void Configure(EntityTypeBuilder<Host> b)
    {
        b.ToTable("host");
        b.HasKey(h => h.Id);

        b.Property(h => h.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(h => h.IdentitySub).HasColumnName("identity_sub");
        b.Property(h => h.DisplayName).HasColumnName("display_name");

        b.Property(h => h.Status).HasColumnName("status").HasConversion(EnumConverters.HostStatus);

        b.HasIndex(h => h.IdentitySub).IsUnique();
    }
}
