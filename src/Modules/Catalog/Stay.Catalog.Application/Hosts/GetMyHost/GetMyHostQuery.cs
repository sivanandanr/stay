using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Hosts.GetMyHost;

/// <summary>Returns the caller's own host profile (resolved from the token subject).</summary>
public sealed record GetMyHostQuery(string OwnerSub) : IQuery<HostResponse>;
