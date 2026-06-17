using Stay.BuildingBlocks.Cqrs;
using Stay.Catalog.Contracts;

namespace Stay.Catalog.Application.Properties.GetPropertyById;

/// <summary>Fetches a single property the caller owns (tenancy-scoped by the token subject, BR-9).</summary>
public sealed record GetPropertyByIdQuery(string OwnerSub, long PropertyId) : IQuery<PropertyResponse>;
