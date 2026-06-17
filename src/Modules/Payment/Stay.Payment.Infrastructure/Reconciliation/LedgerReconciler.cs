using Dapper;
using Npgsql;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure.Reconciliation;

/// <summary>
/// Daily ledger reconciliation (Gate G2): for every local payment that should have settled by the
/// cutoff, compare its status against the PSP's authoritative view and report every divergence. Unlike
/// the poll-reconciler (which settles stuck rows), this is READ-ONLY — its job is to prove "zero
/// unexplained deltas" (§9) and surface anything that isn't for finance, not to mutate state. A
/// non-empty report is the finance alert.
/// </summary>
public sealed class LedgerReconciler(string connectionString, IPaymentGateway gateway)
{
    public async Task<ReconciliationReport> ReconcileAsync(DateTimeOffset cutoff, int batchSize = 500, CancellationToken ct = default)
    {
        List<LocalPayment> payments;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            payments = (await conn.QueryAsync<LocalPayment>(new CommandDefinition("""
                SELECT id AS Id, psp_ref AS PspRef, status AS Status
                FROM payment.payment
                WHERE status IN ('AUTHORIZED','CAPTURED') AND psp_ref IS NOT NULL AND created_at <= @cutoff
                ORDER BY created_at
                LIMIT @batchSize
                """, new { cutoff, batchSize }, cancellationToken: ct))).AsList();
        }

        var deltas = new List<LedgerDelta>();
        foreach (var p in payments)
        {
            var psp = await gateway.GetStatusAsync(p.PspRef!, ct);
            if (Kind(p.Status, psp) is { } kind)
                deltas.Add(new LedgerDelta(p.Id, p.PspRef!, p.Status, psp.ToString(), kind));
        }

        return new ReconciliationReport(payments.Count, deltas);
    }

    /// <summary>Classifies a (local, PSP) status pair; null = agreement (or still legitimately in flight).</summary>
    private static string? Kind(string local, GatewayPaymentStatus psp) => (local, psp) switch
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

    private sealed record LocalPayment(long Id, string? PspRef, string Status);
}
