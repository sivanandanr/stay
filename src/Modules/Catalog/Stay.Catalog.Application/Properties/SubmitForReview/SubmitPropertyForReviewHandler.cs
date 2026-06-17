using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.SubmitForReview;

/// <summary>
/// Submits a draft property for moderation (DRAFT → IN_REVIEW). Owner-scoped (BR-9). A property must
/// have at least one room type to be submittable. Emits <see cref="PropertySubmittedForReview"/> in
/// the same transaction; idempotent if already in review.
/// </summary>
public sealed class SubmitPropertyForReviewHandler(ICatalogDbContext db)
    : ICommandHandler<SubmitPropertyForReviewCommand, PropertyStatusResponse>
{
    public async Task<Result<PropertyStatusResponse>> Handle(SubmitPropertyForReviewCommand command, CancellationToken ct)
    {
        var hostId = await db.Hosts.AsNoTracking()
            .Where(h => h.IdentitySub == command.OwnerSub)
            .Select(h => (long?)h.Id)
            .FirstOrDefaultAsync(ct);

        var property = hostId is null
            ? null
            : await db.Properties.FirstOrDefaultAsync(p => p.Id == command.PropertyId && p.HostId == hostId, ct);

        if (property is null)
            return Error.NotFound("property-not-found", $"Property {command.PropertyId} was not found.");

        switch (property.Status)
        {
            case PropertyStatus.InReview:
                break; // idempotent: already submitted
            case PropertyStatus.Draft:
                if (!await db.RoomTypes.AsNoTracking().AnyAsync(r => r.PropertyId == property.Id, ct))
                    return Error.Conflict("no-room-types", "Add at least one room type before submitting.");
                property.SubmitForReview();
                db.OutboxMessages.Add(CatalogOutboxMessage.From(new PropertySubmittedForReview(
                    Guid.NewGuid(), property.Id, property.HostId, DateTimeOffset.UtcNow)));
                await db.SaveChangesAsync(ct);
                break;
            default:
                return Error.Conflict("invalid-state",
                    $"A {WireFormat.ToWire(property.Status)} property cannot be submitted for review.");
        }

        return Result<PropertyStatusResponse>.Success(
            new PropertyStatusResponse(property.Id, WireFormat.ToWire(property.Status)));
    }
}
