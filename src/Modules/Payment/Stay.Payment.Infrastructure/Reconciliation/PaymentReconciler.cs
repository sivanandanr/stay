using Dapper;
using Npgsql;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure.Reconciliation;

/// <summary>
/// Polls the gateway for payments stuck in a non-terminal state past a staleness threshold and
/// settles their local status — the backstop for webhooks that never arrived (§9). The gateway call
/// happens OUTSIDE any row lock; the update is conditional on the row still being non-terminal, so it
/// races safely with a late webhook and is idempotent (BR-5).
/// </summary>
public sealed class PaymentReconciler(string connectionString, IPaymentGateway gateway)
{
    public async Task<int> ReconcileAsync(TimeSpan staleAfter, DateTimeOffset now, int batchSize = 200, CancellationToken ct = default)
    {
        var cutoff = now - staleAfter;

        List<StalePayment> stale;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            stale = (await conn.QueryAsync<StalePayment>(new CommandDefinition("""
                SELECT id AS Id, psp_ref AS PspRef
                FROM payment.payment
                WHERE status IN ('PENDING','AUTHORIZED') AND psp_ref IS NOT NULL AND updated_at < @cutoff
                ORDER BY updated_at
                LIMIT @batchSize
                """, new { cutoff, batchSize }, cancellationToken: ct))).AsList();
        }

        var reconciled = 0;
        foreach (var payment in stale)
        {
            var settled = Map(await gateway.GetStatusAsync(payment.PspRef!, ct));
            if (settled is null)
                continue; // gateway still non-terminal — leave it for the next pass

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            reconciled += await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE payment.payment
                SET status = @settled, updated_at = now(), row_version = row_version + 1
                WHERE id = @Id AND status IN ('PENDING','AUTHORIZED')
                """, new { settled, payment.Id }, cancellationToken: ct));
        }

        return reconciled;
    }

    private static string? Map(GatewayPaymentStatus status) => status switch
    {
        GatewayPaymentStatus.Captured => "CAPTURED",
        GatewayPaymentStatus.Failed => "FAILED",
        _ => null // Pending/Authorized/Unknown → no settlement yet
    };

    private sealed record StalePayment(long Id, string? PspRef);
}
