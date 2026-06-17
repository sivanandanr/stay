using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Hosts;

namespace Stay.Catalog.Application.Hosts.RegisterHost;

/// <summary>
/// Provisions a host on first login, idempotently and race-safely (BR-5, P0-B4): a replay returns
/// the existing host without re-emitting; concurrent first-requests resolve to exactly one row via
/// the <c>identity_sub</c> unique constraint — the loser re-reads the winner. Only the genuine
/// insert emits <see cref="HostRegistered"/>, in the same transaction (no dual-write, BR-11).
/// </summary>
public sealed class RegisterHostHandler(ICatalogDbContext db)
    : ICommandHandler<RegisterHostCommand, long>
{
    public async Task<Result<long>> Handle(RegisterHostCommand command, CancellationToken ct)
    {
        // Fast path: already provisioned → idempotent no-op, no event.
        var existing = await db.Hosts.AsNoTracking()
            .FirstOrDefaultAsync(h => h.IdentitySub == command.OwnerSub, ct);
        if (existing is not null)
            return Result<long>.Success(existing.Id);

        var host = Host.Register(command.OwnerSub, command.DisplayName);
        db.Hosts.Add(host);

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct); // populates host.Id; throws here if a concurrent insert won

            var @event = new HostRegistered(Guid.NewGuid(), host.Id, command.OwnerSub, DateTimeOffset.UtcNow);
            db.OutboxMessages.Add(CatalogOutboxMessage.From(@event));
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return Result<long>.Success(host.Id);
        }
        catch (DbUpdateException)
        {
            // Lost the race (or replayed concurrently): if the winner now exists, return it (BR-5).
            db.Hosts.Remove(host); // detaches the still-Added, failed insert
            var winner = await db.Hosts.AsNoTracking()
                .FirstOrDefaultAsync(h => h.IdentitySub == command.OwnerSub, ct);
            if (winner is not null)
                return Result<long>.Success(winner.Id);
            throw; // a different, genuinely exceptional failure
        }
    }
}
