using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Hosts;

namespace Stay.Catalog.Application.Hosts.ApproveHost;

/// <summary>
/// Approves a host (→ ACTIVE) and emits <see cref="HostApproved"/> as durable audit evidence in the
/// same transaction (no dual-write, BR-11). Idempotent: re-approving an already-active host is a
/// no-op and emits nothing. Authorization (admin/ops) is enforced at the endpoint.
/// </summary>
public sealed class ApproveHostHandler(ICatalogDbContext db)
    : ICommandHandler<ApproveHostCommand, HostResponse>
{
    public async Task<Result<HostResponse>> Handle(ApproveHostCommand command, CancellationToken ct)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == command.HostId, ct); // tracked
        if (host is null)
            return Error.NotFound("host-not-found", $"Host {command.HostId} was not found.");

        if (host.Status != HostStatus.Active)
        {
            var previous = host.Status;
            host.Approve();
            db.OutboxMessages.Add(CatalogOutboxMessage.From(new HostApproved(
                Guid.NewGuid(), host.Id, command.ActorSub, WireFormat.ToWire(previous), DateTimeOffset.UtcNow)));
            await db.SaveChangesAsync(ct); // UPDATE host + INSERT outbox in one transaction
        }

        return Result<HostResponse>.Success(
            new HostResponse(host.Id, host.DisplayName, WireFormat.ToWire(host.Status)));
    }
}
