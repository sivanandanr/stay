using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.AddRoomType;

/// <summary>
/// Adds a room type to a property the caller owns. Tenancy-scoped server-side (BR-9): the property
/// must belong to the caller's host. Inserts the room type and a <see cref="RoomTypeAdded"/> outbox
/// event in one transaction (no dual-write, BR-11).
/// </summary>
public sealed class AddRoomTypeHandler(ICatalogDbContext db)
    : ICommandHandler<AddRoomTypeCommand, long>
{
    public async Task<Result<long>> Handle(AddRoomTypeCommand command, CancellationToken ct)
    {
        var hostId = await db.Hosts.AsNoTracking()
            .Where(h => h.IdentitySub == command.OwnerSub)
            .Select(h => (long?)h.Id)
            .FirstOrDefaultAsync(ct);

        var ownsProperty = hostId is not null && await db.Properties.AsNoTracking()
            .AnyAsync(p => p.Id == command.PropertyId && p.HostId == hostId, ct);

        // Same response whether the property doesn't exist or isn't the caller's — don't leak existence.
        if (!ownsProperty)
            return Error.NotFound("property-not-found", $"Property {command.PropertyId} was not found.");

        // UnitKind is validated upstream, so this parse always succeeds.
        UnitKindMap.TryParse(command.UnitKind, out var unitKind);

        var bedConfig = command.BedConfig is null
            ? null
            : new BedConfig(command.BedConfig.Doubles, command.BedConfig.Singles, command.BedConfig.Sofabeds);

        var roomType = RoomType.Create(
            propertyId: command.PropertyId,
            name: command.Name,
            unitKind: unitKind,
            totalUnits: command.TotalUnits,
            baseOccupancy: command.BaseOccupancy,
            maxOccupancy: command.MaxOccupancy,
            maxAdults: command.MaxAdults,
            maxChildren: command.MaxChildren,
            bedConfig: bedConfig,
            sizeSqm: command.SizeSqm);

        await using var tx = await db.BeginTransactionAsync(ct);

        db.RoomTypes.Add(roomType);
        await db.SaveChangesAsync(ct); // populates roomType.Id

        var @event = new RoomTypeAdded(Guid.NewGuid(), roomType.Id, command.PropertyId, DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(CatalogOutboxMessage.From(@event));
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        return Result<long>.Success(roomType.Id);
    }
}
