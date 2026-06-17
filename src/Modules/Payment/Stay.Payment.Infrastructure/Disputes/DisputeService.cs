using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure.Disputes;

/// <summary>
/// Chargeback / dispute handling (Phase 8). Opening is idempotent by the PSP dispute id (webhooks
/// redeliver, BR-5). Resolution is a privileged finance action: it transitions to a terminal outcome
/// with a mandatory note and emits an audit-evidence event in the same transaction (§10). The PSP
/// remains the source of truth for the dispute lifecycle; Stay records state and reacts.
/// </summary>
public sealed class DisputeService(string connectionString)
{
    private static readonly HashSet<string> Outcomes = ["WON", "LOST", "ACCEPTED"];

    public async Task<Result<DisputeResponse>> OpenAsync(OpenDisputeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PspDisputeId))
            return Error.Validation("A PSP dispute id is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var paymentExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM payment.payment WHERE id = @PaymentId)", new { request.PaymentId }, cancellationToken: ct));
        if (!paymentExists)
            return Error.NotFound("payment-not-found", $"Payment {request.PaymentId} was not found.");

        // Idempotent by PSP dispute id: a redelivered webhook returns the existing dispute unchanged.
        var existing = await conn.QuerySingleOrDefaultAsync<DisputeRow>(new CommandDefinition("""
            SELECT id AS Id, payment_id AS PaymentId, status AS Status, amount AS Amount, currency AS Currency
            FROM payment.dispute WHERE psp_dispute_id = @PspDisputeId
            """, new { request.PspDisputeId }, cancellationToken: ct));
        if (existing is not null)
            return Result<DisputeResponse>.Success(ToResponse(existing));

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO payment.dispute (payment_id, psp_dispute_id, reason, amount, currency)
            VALUES (@PaymentId, @PspDisputeId, @Reason, @Amount, @Currency)
            RETURNING id
            """, request, cancellationToken: ct));

        return Result<DisputeResponse>.Success(
            new DisputeResponse(id, request.PaymentId, "OPEN", request.Amount, request.Currency));
    }

    public async Task<Result<DisputeResponse>> ResolveAsync(
        long disputeId, string actorSub, string outcome, string resolution, CancellationToken ct = default)
    {
        if (!Outcomes.Contains(outcome))
            return Error.Validation($"Outcome must be one of {string.Join(", ", Outcomes)}.");
        if (string.IsNullOrWhiteSpace(resolution))
            return Error.Validation("A resolution note is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var dispute = await conn.QuerySingleOrDefaultAsync<DisputeRow>(new CommandDefinition("""
            SELECT id AS Id, payment_id AS PaymentId, status AS Status, amount AS Amount, currency AS Currency
            FROM payment.dispute WHERE id = @disputeId FOR UPDATE
            """, new { disputeId }, tx, cancellationToken: ct));

        if (dispute is null)
            return Error.NotFound("dispute-not-found", $"Dispute {disputeId} was not found.");
        if (dispute.Status == outcome)
            return Result<DisputeResponse>.Success(ToResponse(dispute)); // idempotent
        if (dispute.Status is not ("OPEN" or "UNDER_REVIEW"))
            return Error.Conflict("invalid-state", $"A {dispute.Status} dispute cannot be resolved.");

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.dispute SET status = @outcome, resolution = @resolution, resolved_at = now() WHERE id = @disputeId",
            new { outcome, resolution, disputeId }, tx, cancellationToken: ct));

        var @event = new DisputeResolved(
            Guid.NewGuid(), disputeId, dispute.PaymentId, outcome, actorSub, resolution, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO payment.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<DisputeResponse>.Success(ToResponse(dispute with { Status = outcome }));
    }

    private static DisputeResponse ToResponse(DisputeRow r) => new(r.Id, r.PaymentId, r.Status, r.Amount, r.Currency);

    private sealed record DisputeRow(long Id, long PaymentId, string Status, decimal Amount, string Currency);
}
