using Dapper;
using Npgsql;

namespace Stay.Payment.Infrastructure.Webhooks;

/// <summary>A verified webhook forwarded by the PaymentGateway service (it checks the PSP signature, §9).</summary>
public sealed record PaymentWebhook(string Psp, string PspEventId, string Type, string PspRef, string Payload);

public enum WebhookOutcome
{
    Processed,  // recorded and applied
    Duplicate,  // already seen this provider event id — no-op
    Ignored     // recorded, but the type isn't one we act on
}

/// <summary>
/// Ingests payment webhooks — the source of truth for async PSP state (§9). Idempotent by provider
/// event id: <c>payment.webhook_event</c> is unique on <c>(psp, psp_event_id)</c>, so a redelivery is
/// recorded once and applied once (BR-5). A poll-reconciler (future) covers missed webhooks.
/// </summary>
public sealed class PaymentWebhookService(string connectionString)
{
    public async Task<WebhookOutcome> IngestAsync(PaymentWebhook webhook, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO payment.webhook_event (psp, psp_event_id, type, payload)
            VALUES (@Psp, @PspEventId, @Type, CAST(@Payload AS jsonb))
            ON CONFLICT (psp, psp_event_id) DO NOTHING
            """, webhook, tx, cancellationToken: ct));

        if (inserted == 0)
        {
            await tx.RollbackAsync(ct);
            return WebhookOutcome.Duplicate; // already ingested this provider event
        }

        var outcome = webhook.Type switch
        {
            "payment.captured" => await TransitionPaymentAsync(conn, tx, webhook.PspRef, "CAPTURED", ct),
            "payment.failed" => await TransitionPaymentAsync(conn, tx, webhook.PspRef, "FAILED", ct),
            "refund.processed" => await ProcessRefundAsync(conn, tx, webhook.PspRef, ct),
            _ => WebhookOutcome.Ignored
        };

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.webhook_event SET processed_at = now() WHERE psp = @Psp AND psp_event_id = @PspEventId",
            webhook, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return outcome;
    }

    // Only advance from a non-terminal state — the webhook is authoritative but must not regress a refund.
    private static async Task<WebhookOutcome> TransitionPaymentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string pspRef, string status, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE payment.payment
            SET status = @status, updated_at = now(), row_version = row_version + 1
            WHERE psp_ref = @pspRef AND status IN ('PENDING','AUTHORIZED')
            """, new { status, pspRef }, tx, cancellationToken: ct));
        return WebhookOutcome.Processed;
    }

    private static async Task<WebhookOutcome> ProcessRefundAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string pspRef, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE payment.refund SET status = 'SUCCEEDED'
            WHERE status = 'PENDING'
              AND payment_id = (SELECT id FROM payment.payment WHERE psp_ref = @pspRef)
            """, new { pspRef }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE payment.payment
            SET status = 'REFUNDED', updated_at = now(), row_version = row_version + 1
            WHERE psp_ref = @pspRef
            """, new { pspRef }, tx, cancellationToken: ct));

        return WebhookOutcome.Processed;
    }
}
