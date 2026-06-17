using Microsoft.EntityFrameworkCore;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Application.Persistence;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Hosts.GetMyHost;

public sealed class GetMyHostHandler(ICatalogDbContext db)
    : IQueryHandler<GetMyHostQuery, HostResponse>
{
    public async Task<Result<HostResponse>> Handle(GetMyHostQuery query, CancellationToken ct)
    {
        var host = await db.Hosts.AsNoTracking()
            .FirstOrDefaultAsync(h => h.IdentitySub == query.OwnerSub, ct);

        if (host is null)
            return Error.NotFound("host-not-registered", "No host profile exists for the current user.");

        return Result<HostResponse>.Success(
            new HostResponse(host.Id, host.DisplayName, WireFormat.ToWire(host.Status)));
    }
}
