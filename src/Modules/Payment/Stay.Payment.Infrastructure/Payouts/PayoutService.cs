using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure.Payouts;

/// <summary>
/// Owner payouts via Razorpay Route (§9, Phase 8). Generation builds a DRAFT statement with per-booking
/// commission lines; execution pays the net to the host's linked account through <see cref="IPayoutGateway"/>
/// and records the outcome. Idempotent throughout: a statement is unique per (host, period), and the
/// payout call carries <c>stay:payout:{id}</c> so replays never double-pay. A failed payout is recorded
/// (FAILED) — never silently lost — and a <see cref="PayoutCompleted"/> event is emitted either way for
/// the §10 audit trail. Money is <c>NUMERIC</c>; commission rounds half-away-from-zero to the minor unit.
/// </summary>
public sealed class PayoutService(string connectionString, IPayoutGateway gateway)
{
    static PayoutService() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    public async Task<Result<PayoutResponse>> GenerateAsync(GeneratePayoutRequest request, CancellationToken ct = default)
    {
        if (request.Lines.Count == 0)
            return Error.Validation("A payout needs at least one earning line.");
        if (request.CommissionPct is < 0 or > 100)
            return Error.Validation("Commission percent must be between 0 and 100.");
        if (request.PeriodEnd < request.PeriodStart)
            return Error.Validation("The statement period end cannot precede its start.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotent per (host, period): return the existing statement instead of duplicating.
        var existing = await conn.QuerySingleOrDefaultAsync<PayoutRow>(new CommandDefinition("""
            SELECT id AS Id, host_id AS HostId, gross_amount AS GrossAmount, commission AS Commission,
                   net_amount AS NetAmount, currency AS Currency, status AS Status, statement_ref AS StatementRef
            FROM payment.payout
            WHERE host_id = @HostId AND period_start = @PeriodStart AND period_end = @PeriodEnd
            """, new { request.HostId, request.PeriodStart, request.PeriodEnd }, tx, cancellationToken: ct));
        if (existing is not null)
            return Result<PayoutResponse>.Success(ToResponse(existing));

        var lines = request.Lines
            .Select(l =>
            {
                var commission = Math.Round(l.Gross * request.CommissionPct / 100m, 2, MidpointRounding.AwayFromZero);
                return (l.BookingId, l.Gross, Commission: commission, Net: l.Gross - commission);
            })
            .ToList();

        var gross = lines.Sum(l => l.Gross);
        var commissionTotal = lines.Sum(l => l.Commission);
        var net = gross - commissionTotal;

        var payoutId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO payment.payout (host_id, period_start, period_end, gross_amount, commission, net_amount, currency, status)
            VALUES (@HostId, @PeriodStart, @PeriodEnd, @gross, @commissionTotal, @net, @Currency, 'DRAFT')
            RETURNING id
            """, new { request.HostId, request.PeriodStart, request.PeriodEnd, gross, commissionTotal, net, request.Currency },
            tx, cancellationToken: ct));

        foreach (var line in lines)
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO payment.payout_line (payout_id, booking_id, gross, commission, net)
                VALUES (@payoutId, @BookingId, @Gross, @Commission, @Net)
                """, new { payoutId, line.BookingId, line.Gross, line.Commission, line.Net }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Result<PayoutResponse>.Success(
            new PayoutResponse(payoutId, request.HostId, gross, commissionTotal, net, request.Currency, "DRAFT"));
    }

    public async Task<Result<PayoutExecutionResponse>> ExecuteAsync(
        long payoutId, string linkedAccountRef, string actorSub, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(linkedAccountRef))
            return Error.Validation("A linked-account reference is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var payout = await conn.QuerySingleOrDefaultAsync<PayoutRow>(new CommandDefinition("""
            SELECT id AS Id, host_id AS HostId, gross_amount AS GrossAmount, commission AS Commission,
                   net_amount AS NetAmount, currency AS Currency, status AS Status, statement_ref AS StatementRef
            FROM payment.payout WHERE id = @payoutId FOR UPDATE
            """, new { payoutId }, tx, cancellationToken: ct));

        if (payout is null)
            return Error.NotFound("payout-not-found", $"Payout {payoutId} was not found.");
        if (payout.Status == "PAID")
            return Result<PayoutExecutionResponse>.Success(
                new PayoutExecutionResponse(payoutId, "PAID", payout.StatementRef, null)); // idempotent
        if (payout.Status is not ("DRAFT" or "SCHEDULED" or "FAILED"))
            return Error.Conflict("invalid-state", $"A {payout.Status} payout cannot be executed.");

        var result = await gateway.PayoutAsync(
            new PayoutInstruction($"stay:payout:{payoutId}", linkedAccountRef, payout.NetAmount, payout.Currency), ct);

        var status = result.Paid ? "PAID" : "FAILED";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE payment.payout SET status = @status, statement_ref = @ref WHERE id = @payoutId",
            new { status, @ref = result.PspRef, payoutId }, tx, cancellationToken: ct));

        var @event = new PayoutCompleted(
            Guid.NewGuid(), payoutId, payout.HostId, payout.NetAmount, payout.Currency, status, actorSub, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO payment.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        // A failed payout is a recorded outcome (queue a finance retry), not a request error.
        return Result<PayoutExecutionResponse>.Success(
            new PayoutExecutionResponse(payoutId, status, result.PspRef, result.FailureReason));
    }

    private static PayoutResponse ToResponse(PayoutRow r) =>
        new(r.Id, r.HostId, r.GrossAmount, r.Commission, r.NetAmount, r.Currency, r.Status);

    private sealed record PayoutRow(
        long Id, long HostId, decimal GrossAmount, decimal Commission, decimal NetAmount,
        string Currency, string Status, string? StatementRef = null);
}
