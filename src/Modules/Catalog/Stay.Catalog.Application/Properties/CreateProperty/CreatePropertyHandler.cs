using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.CreateProperty;

/// <summary>
/// Creates a DRAFT property for an approved owner. Enforces the owner-approval gate server-side
/// (CLAUDE.md §12), then inserts the property and a <see cref="PropertyCreated"/> outbox event in
/// one transaction (no dual-write, BR-11).
/// </summary>
public sealed class CreatePropertyHandler(ICatalogDbContext db)
    : ICommandHandler<CreatePropertyCommand, long>
{
    public async Task<Result<long>> Handle(CreatePropertyCommand command, CancellationToken ct)
    {
        // Authorization: resolve the owner from the token subject and enforce the approval gate.
        var host = await db.Hosts.SingleOrDefaultAsync(h => h.IdentitySub == command.OwnerSub, ct);
        if (host is null)
            return Error.Forbidden("host-not-registered", "No host profile exists for the current user.");
        if (!host.CanList)
            return Error.Forbidden("owner-not-approved", "Your host account is not approved to create listings.");

        // Referential check kept inside the catalog context (no cross-context lookups). The city name
        // travels on the PropertyCreated event so the search indexer needn't read it back.
        var city = await db.Cities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == command.CityId, ct);
        if (city is null)
            return Error.NotFound("city-not-found", $"City {command.CityId} does not exist.");

        // PropertyType is validated upstream, so this parse always succeeds.
        PropertyTypeMap.TryParse(command.PropertyType, out var propertyType);

        var address = new Domain.Properties.Address(
            command.Address.Line1,
            command.Address.Line2,
            command.Address.City,
            command.Address.Region,
            command.Address.PostalCode,
            command.Address.CountryCode);

        var property = Property.Create(
            hostId: host.Id,
            name: command.Name,
            type: propertyType,
            description: command.Description,
            starRating: command.StarRating,
            latitude: command.Latitude,
            longitude: command.Longitude,
            countryCode: command.CountryCode,
            cityId: command.CityId,
            address: address,
            defaultCurrency: command.DefaultCurrency,
            timezone: command.Timezone,
            checkInTime: command.CheckInTime,
            checkOutTime: command.CheckOutTime);

        await using var tx = await db.BeginTransactionAsync(ct);

        db.Properties.Add(property);
        await db.SaveChangesAsync(ct); // populates the DB-generated property.Id

        var @event = new PropertyCreated(
            Guid.NewGuid(), property.Id, host.Id, property.Name, command.PropertyType,
            property.CountryCode, property.CityId, city.Name, command.Latitude, command.Longitude,
            DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(CatalogOutboxMessage.From(@event));
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        return Result<long>.Success(property.Id);
    }
}
