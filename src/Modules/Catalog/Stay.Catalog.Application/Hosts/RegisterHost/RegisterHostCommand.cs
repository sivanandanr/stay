using Stay.BuildingBlocks.Cqrs;

namespace Stay.Catalog.Application.Hosts.RegisterHost;

/// <summary>
/// First-login owner provisioning. <see cref="OwnerSub"/> is the token subject (never client-supplied);
/// returns the host id. Idempotent and race-safe (BR-5).
/// </summary>
public sealed record RegisterHostCommand(string OwnerSub, string DisplayName) : ICommand<long>;
