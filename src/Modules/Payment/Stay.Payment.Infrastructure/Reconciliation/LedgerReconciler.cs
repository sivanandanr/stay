using Dapper;
using Npgsql;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure.Reconciliation;

/// <summary>
/// Daily ledger reconciliation (Gate G2): for every local money record that should have settled by the
/// cutoff — payments, refunds, and payouts — compare its status against the PSP's authoritative view and
/// report every divergence. Unlike the poll-reconciler (which settles stuck rows), this is READ-ONLY —
/// its job is to prove "zero unexplained deltas" across <c>payment</c>/<c>refund</c>/<c>payout</c> (§9)
/// and surface anything that isn't for finance, not to mutate state. A non-empty report is the alert.
/// </summary>
public sealed class LedgerReconciler(string connectionString, IPaymentGateway gateway, IPayoutGateway payoutGateway)
{
    public async Task<ReconciliationReport> ReconcileAsync(DateTimeOffset cutoff, int batchSize = 500, CancellationToken ct = default)
    {
        List<LocalRow> payments, refunds, payouts;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            payments = (await conn.QueryAsync<LocalRow>(new CommandDefinition("""
                SELECT id AS Id, psp_ref AS Ref, status AS Status
                FROM payment.payment
                WHERE status IN ('AUTHORIZED','CAPTURED') AND psp_ref IS NOT NULL AND created_at <= @cutoff
                ORDER BY created_at LIMIT @batchSize
                """, new { cutoff, batchSize }, cancellationToken: ct))).AsList();

            refunds = (await conn.QueryAsync<LocalRow>(new CommandDefinition("""
                SELECT id AS Id, psp_ref AS Ref, status AS Status
                FROM payment.refund
                WHERE status = 'SUCCEEDED' AND psp_ref IS NOT NULL AND created_at <= @cutoff
                ORDER BY created_at LIMIT @batchSize
                """, new { cutoff, batchSize }, cancellationToken: ct))).AsList();

            payouts = (await conn.QueryAsync<LocalRow>(new CommandDefinition("""
                SELECT id AS Id, statement_ref AS Ref, status AS Status
                FROM payment.payout
                WHERE status = 'PAID' AND statement_ref IS NOT NULL AND created_at <= @cutoff
                ORDER BY created_at LIMIT @batchSize
                """, new { cutoff, batchSize }, cancellationToken: ct))).AsList();
        }

        var deltas = new List<LedgerDelta>();

        foreach (var p in payments)
        {
            var psp = await gateway.GetStatusAsync(p.Ref!, ct);
            if (PaymentKind(p.Status, psp) is { } kind)
                deltas.Add(new LedgerDelta("payment", p.Id, p.Ref!, p.Status, psp.ToString(), kind));
        }

        foreach (var r in refunds)
        {
            var psp = await gateway.GetRefundStatusAsync(r.Ref!, ct);
            if (psp != GatewayRefundStatus.Processed)
                deltas.Add(new LedgerDelta("refund", r.Id, r.Ref!, r.Status, psp.ToString(),
                    psp == GatewayRefundStatus.Unknown ? "psp-unknown" : "local-refunded-psp-not"));
        }

        foreach (var po in payouts)
        {
            var psp = await payoutGateway.GetPayoutStatusAsync(po.Ref!, ct);
            if (psp != GatewayPayoutStatus.Paid)
                deltas.Add(new LedgerDelta("payout", po.Id, po.Ref!, po.Status, psp.ToString(),
                    psp == GatewayPayoutStatus.Unknown ? "psp-unknown" : "local-paid-psp-not"));
        }

        return new ReconciliationReport(payments.Count + refunds.Count + payouts.Count, deltas);
    }

    /// <summary>Classifies a payment (local, PSP) status pair; null = agreement (or still legitimately in flight).</summary>
    private static string? PaymentKind(string local, GatewayPaymentStatus psp) => (local, psp) switch
    {
        ("CAPTURED", GatewayPaymentStatus.Captured) => null,                 // matched
        ("AUTHORIZED", GatewayPaymentStatus.Authorized) => null,            // still in flight, fine
        ("AUTHORIZED", GatewayPaymentStatus.Pending) => null,
        ("CAPTURED", _) => "local-captured-psp-not",                         // we think money landed; PSP disagrees
        ("AUTHORIZED", GatewayPaymentStatus.Captured) => "psp-captured-local-not", // missed capture webhook
        ("AUTHORIZED", GatewayPaymentStatus.Failed) => "psp-failed-local-authorized",
        (_, GatewayPaymentStatus.Unknown) => "psp-unknown",
        _ => "status-divergence"
    };

    private sealed record LocalRow(long Id, string? Ref, string Status);
}
