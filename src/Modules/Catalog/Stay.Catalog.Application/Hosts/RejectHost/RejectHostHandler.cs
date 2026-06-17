using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Hosts;

namespace Stay.Catalog.Application.Hosts.RejectHost;

/// <summary>
/// Rejects a host (→ SUSPENDED) and emits <see cref="HostRejected"/> (with the mandatory reason) as
/// durable audit evidence in the same transaction. Idempotent: rejecting an already-suspended host
/// is a no-op. Authorization (admin/ops) is enforced at the endpoint.
/// </summary>
public sealed class RejectHostHandler(ICatalogDbContext db)
    : ICommandHandler<RejectHostCommand, HostResponse>
{
    public async Task<Result<HostResponse>> Handle(RejectHostCommand command, CancellationToken ct)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == command.HostId, ct); // tracked
        if (host is null)
            return Error.NotFound("host-not-found", $"Host {command.HostId} was not found.");

        if (host.Status != HostStatus.Suspended)
        {
            var previous = host.Status;
            host.Reject();
            db.OutboxMessages.Add(CatalogOutboxMessage.From(new HostRejected(
                Guid.NewGuid(), host.Id, command.ActorSub, WireFormat.ToWire(previous), command.Reason,
                DateTimeOffset.UtcNow)));
            await db.SaveChangesAsync(ct);
        }

        return Result<HostResponse>.Success(
            new HostResponse(host.Id, host.DisplayName, WireFormat.ToWire(host.Status)));
    }
}
