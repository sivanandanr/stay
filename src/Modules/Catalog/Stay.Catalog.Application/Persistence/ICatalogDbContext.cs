using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stay.Catalog.Domain.Geo;
using Stay.Catalog.Domain.Hosts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Persistence;

/// <summary>
/// Write-side persistence port for the catalog context. Defined in Application so handlers stay
/// free of the Infrastructure project; the concrete EF <c>CatalogDbContext</c> implements it.
/// One <see cref="SaveChangesAsync"/> call is the transaction boundary.
/// </summary>
public interface ICatalogDbContext
{
    DbSet<Property> Properties { get; }
    DbSet<RoomType> RoomTypes { get; }
    DbSet<Host> Hosts { get; }
    DbSet<City> Cities { get; }
    DbSet<CatalogOutboxMessage> OutboxMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Opens a DB transaction so the state change and its outbox event commit together.</summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
