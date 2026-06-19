using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Domain.Geo;
using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Infrastructure.Persistence;

/// <summary>EF Core context for the catalog bounded context (owns the <c>catalog</c> schema).</summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options), ICatalogDbContext
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<Amenity> Amenities => Set<Amenity>();
    public DbSet<PropertyAmenity> PropertyAmenities => Set<PropertyAmenity>();
    public DbSet<Host> Hosts => Set<Host>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<CatalogOutboxMessage> OutboxMessages => Set<CatalogOutboxMessage>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        Database.BeginTransactionAsync(ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}
