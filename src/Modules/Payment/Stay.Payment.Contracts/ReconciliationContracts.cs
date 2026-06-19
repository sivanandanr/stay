namespace Stay.Payment.Contracts;

/// <summary>
/// A divergence between a local money record (payment / refund / payout) and the PSP's view of it
/// (Gate G2). <see cref="EntityType"/> distinguishes the ledger; <see cref="EntityId"/> is that row's id.
/// </summary>
public sealed record LedgerDelta(
    string EntityType, long EntityId, string Ref, string LocalStatus, string GatewayStatus, string Kind);

/// <summary>
/// Outcome of a daily ledger reconciliation: how many payments were checked against the PSP and which
/// diverged. <see cref="Balanced"/> is the Gate G2 condition — zero unexplained deltas (§9). Deltas are
/// surfaced for finance to action; the run itself never mutates (the poll-reconciler settles state).
/// </summary>
public sealed record ReconciliationReport(int Checked, IReadOnlyList<LedgerDelta> Deltas)
{
    public bool Balanced => Deltas.Count == 0;
}
