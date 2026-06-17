namespace Stay.Catalog.Domain.Hosts;

/// <summary>Mirrors the <c>status</c> CHECK on <c>catalog.host</c>.</summary>
public enum HostStatus
{
    Pending,
    Active,
    Suspended
}

/// <summary>
/// Platform-owned host record keyed by the external identity <c>sub</c>. A self-registering owner
/// starts <see cref="HostStatus.Pending"/> and may only list once an admin approves them
/// (CLAUDE.md §12). Full host lifecycle (approval/suspension) is built out in Phase 1.
/// </summary>
public sealed class Host
{
    private Host() { } // EF materialization

    public long Id { get; private set; }
    public string IdentitySub { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public HostStatus Status { get; private set; }

    /// <summary>An approved, active host may create listings; anyone else is rejected server-side.</summary>
    public bool CanList => Status == HostStatus.Active;

    /// <summary>
    /// First-login provisioning: creates a host for the given identity subject, pending approval.
    /// Other columns (kyc_status, timestamps, row_version) take their database defaults.
    /// </summary>
    public static Host Register(string identitySub, string displayName) => new()
    {
        IdentitySub = identitySub,
        DisplayName = displayName.Trim(),
        Status = HostStatus.Pending
    };

    /// <summary>Admin decision: the host is approved and may now list.</summary>
    public void Approve() => Status = HostStatus.Active;

    /// <summary>Admin decision: the host is rejected/suspended and may not list. (No distinct
    /// REJECTED state exists on <c>catalog.host.status</c>; SUSPENDED is the non-operating state.)</summary>
    public void Reject() => Status = HostStatus.Suspended;
}
