using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.SetAmenities;

/// <summary>
/// Replaces a property's amenity set with the given codes. Tenancy-scoped server-side (BR-9): the
/// property must belong to the caller's host. Unknown codes are rejected as a validation error.
/// Replaces the join rows and emits <see cref="PropertyAmenitiesUpdated"/> (carrying the full code
/// list) in one transaction (no dual-write, BR-11) — the search read model overwrites its amenities
/// from the event.
/// </summary>
public sealed class SetPropertyAmenitiesHandler(ICatalogDbContext db)
    : ICommandHandler<SetPropertyAmenitiesCommand, int>
{
    public async Task<Result<int>> Handle(SetPropertyAmenitiesCommand command, CancellationToken ct)
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

        // Normalize: trim, upper-case, de-dupe. Codes are stable tokens (e.g. WIFI, POOL).
        var codes = command.AmenityCodes
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

        // Resolve to amenity ids; reject any unknown code so the index never gets a phantom amenity.
        var known = await db.Amenities.AsNoTracking()
            .Where(a => codes.Contains(a.Code))
            .Select(a => new { a.Id, a.Code })
            .ToListAsync(ct);

        if (known.Count != codes.Count)
        {
            var unknown = codes.Except(known.Select(k => k.Code));
            return Error.Validation($"Unknown amenity code(s): {string.Join(", ", unknown)}.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);

        var existing = await db.PropertyAmenities
            .Where(pa => pa.PropertyId == command.PropertyId)
            .ToListAsync(ct);
        db.PropertyAmenities.RemoveRange(existing);
        foreach (var amenity in known)
            db.PropertyAmenities.Add(PropertyAmenity.Link(command.PropertyId, amenity.Id));

        var @event = new PropertyAmenitiesUpdated(
            Guid.NewGuid(), command.PropertyId, known.Select(k => k.Code).ToList(), DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(CatalogOutboxMessage.From(@event));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Result<int>.Success(known.Count);
    }
}
