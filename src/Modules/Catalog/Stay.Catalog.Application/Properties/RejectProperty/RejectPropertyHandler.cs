using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;

namespace Stay.Catalog.Application.Properties.RejectProperty;

/// <summary>
/// Rejects a property in review back to DRAFT and emits <see cref="PropertyRejected"/> (with the
/// mandatory reason) as audit evidence in the same transaction. Idempotent if already a draft.
/// Authorization (ops) is at the endpoint.
/// </summary>
public sealed class RejectPropertyHandler(ICatalogDbContext db)
    : ICommandHandler<RejectPropertyCommand, PropertyStatusResponse>
{
    public async Task<Result<PropertyStatusResponse>> Handle(RejectPropertyCommand command, CancellationToken ct)
    {
        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == command.PropertyId, ct);
        if (property is null)
            return Error.NotFound("property-not-found", $"Property {command.PropertyId} was not found.");

        switch (property.Status)
        {
            case PropertyStatus.Draft:
                break; // idempotent: already back to draft
            case PropertyStatus.InReview:
                property.ReturnToDraft();
                db.OutboxMessages.Add(CatalogOutboxMessage.From(new PropertyRejected(
                    Guid.NewGuid(), property.Id, command.ActorSub, command.Reason, DateTimeOffset.UtcNow)));
                await db.SaveChangesAsync(ct);
                break;
            default:
                return Error.Conflict("invalid-state",
                    $"A {WireFormat.ToWire(property.Status)} property cannot be rejected.");
        }

        return Result<PropertyStatusResponse>.Success(
            new PropertyStatusResponse(property.Id, WireFormat.ToWire(property.Status)));
    }
}
