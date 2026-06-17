using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.PublishProperty;

/// <summary>
/// Publishes a property in review (IN_REVIEW → LIVE) and emits <see cref="PropertyPublished"/> as
/// audit evidence in the same transaction. Idempotent if already live. Authorization (ops) is at the
/// endpoint.
/// </summary>
public sealed class PublishPropertyHandler(ICatalogDbContext db)
    : ICommandHandler<PublishPropertyCommand, PropertyStatusResponse>
{
    public async Task<Result<PropertyStatusResponse>> Handle(PublishPropertyCommand command, CancellationToken ct)
    {
        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == command.PropertyId, ct);
        if (property is null)
            return Error.NotFound("property-not-found", $"Property {command.PropertyId} was not found.");

        switch (property.Status)
        {
            case PropertyStatus.Live:
                break; // idempotent
            case PropertyStatus.InReview:
                property.Publish();
                db.OutboxMessages.Add(CatalogOutboxMessage.From(new PropertyPublished(
                    Guid.NewGuid(), property.Id, command.ActorSub, DateTimeOffset.UtcNow)));
                await db.SaveChangesAsync(ct);
                break;
            default:
                return Error.Conflict("invalid-state",
                    $"A {WireFormat.ToWire(property.Status)} property cannot be published.");
        }

        return Result<PropertyStatusResponse>.Success(
            new PropertyStatusResponse(property.Id, WireFormat.ToWire(property.Status)));
    }
}
