using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.GetPropertyById;

public sealed class GetPropertyByIdHandler(ICatalogDbContext db)
    : IQueryHandler<GetPropertyByIdQuery, PropertyResponse>
{
    public async Task<Result<PropertyResponse>> Handle(GetPropertyByIdQuery query, CancellationToken ct)
    {
        // Resolve the caller's host; absence means they own nothing.
        var hostId = await db.Hosts.AsNoTracking()
            .Where(h => h.IdentitySub == query.OwnerSub)
            .Select(h => (long?)h.Id)
            .FirstOrDefaultAsync(ct);

        var property = hostId is null
            ? null
            : await db.Properties.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == query.PropertyId && p.HostId == hostId, ct);

        // Same response whether it doesn't exist or isn't the caller's — don't leak existence.
        if (property is null)
            return Error.NotFound("property-not-found", $"Property {query.PropertyId} was not found.");

        var response = new PropertyResponse(
            Id: property.Id,
            HostId: property.HostId,
            Name: property.Name,
            PropertyType: WireFormat.ToWire(property.Type),
            Status: WireFormat.ToWire(property.Status),
            Description: property.Description,
            StarRating: property.StarRating,
            Latitude: property.Geo.Y,
            Longitude: property.Geo.X,
            CountryCode: property.CountryCode,
            CityId: property.CityId,
            Address: new AddressDto(
                property.Address.Line1,
                property.Address.Line2,
                property.Address.City,
                property.Address.Region,
                property.Address.PostalCode,
                property.Address.CountryCode),
            DefaultCurrency: property.DefaultCurrency,
            Timezone: property.Timezone,
            CheckInTime: property.CheckInTime,
            CheckOutTime: property.CheckOutTime,
            CreatedAt: property.CreatedAt);

        return Result<PropertyResponse>.Success(response);
    }
}
