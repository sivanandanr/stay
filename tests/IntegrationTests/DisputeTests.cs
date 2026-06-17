using Dapper;
using Npgsql;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure.Disputes;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / §10 — disputes: opening is idempotent by PSP dispute id (webhooks redeliver), resolution
/// transitions to a terminal outcome with a mandatory note and emits an audit event, idempotently.
/// </summary>
public sealed class DisputeTests : IAsyncLifetime
{
    private const string Actor = "finance|1";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private DisputeService _disputes = null!;
    private long _paymentId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _paymentId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO payment.payment (booking_id, psp, psp_ref, amount, currency, status, idempotency_key)
            VALUES (1, 'razorpay', 'pay_1', 1000.00, 'INR', 'CAPTURED', 'k1') RETURNING id
            """);
        _disputes = new DisputeService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private OpenDisputeRequest Open(string pspDisputeId = "disp_1") =>
        new(_paymentId, pspDisputeId, 1000.00m, "INR", "fraudulent");

    private async Task<(string Status, int Outbox)> StateAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var status = await conn.ExecuteScalarAsync<string>("SELECT status FROM payment.dispute WHERE id = @id", new { id });
        var outbox = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM payment.outbox_message");
        return (status!, outbox);
    }

    [Fact]
    public async Task Opening_records_the_dispute()
    {
        var result = await _disputes.OpenAsync(Open());

        Assert.True(result.IsSuccess);
        Assert.Equal("OPEN", result.Value!.Status);
    }

    [Fact]
    public async Task Opening_is_idempotent_by_psp_dispute_id()
    {
        var first = await _disputes.OpenAsync(Open("disp_x"));
        var second = await _disputes.OpenAsync(Open("disp_x"));

        Assert.Equal(first.Value!.Id, second.Value!.Id); // same dispute, not duplicated
    }

    [Fact]
    public async Task Opening_against_an_unknown_payment_is_not_found()
    {
        var result = await _disputes.OpenAsync(new OpenDisputeRequest(999_999, "d", 10m, "INR", null));

        Assert.False(result.IsSuccess);
        Assert.Equal("payment-not-found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Resolving_sets_the_outcome_and_emits_an_audit_event()
    {
        var dispute = (await _disputes.OpenAsync(Open())).Value!;

        var result = await _disputes.ResolveAsync(dispute.Id, Actor, "WON", "Evidence submitted; bank reversed.");

        Assert.Equal("WON", result.Value!.Status);
        var state = await StateAsync(dispute.Id);
        Assert.Equal("WON", state.Status);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task Resolving_is_idempotent_and_does_not_emit_twice()
    {
        var dispute = (await _disputes.OpenAsync(Open())).Value!;

        await _disputes.ResolveAsync(dispute.Id, Actor, "LOST", "Lost on merits.");
        var second = await _disputes.ResolveAsync(dispute.Id, Actor, "LOST", "Lost on merits.");

        Assert.Equal("LOST", second.Value!.Status);
        Assert.Equal(1, (await StateAsync(dispute.Id)).Outbox);
    }

    [Fact]
    public async Task An_invalid_outcome_is_rejected()
    {
        var dispute = (await _disputes.OpenAsync(Open())).Value!;

        var result = await _disputes.ResolveAsync(dispute.Id, Actor, "MAYBE", "x");

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }

    [Fact]
    public async Task A_resolution_note_is_mandatory()
    {
        var dispute = (await _disputes.OpenAsync(Open())).Value!;

        var result = await _disputes.ResolveAsync(dispute.Id, Actor, "WON", "  ");

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }
}
