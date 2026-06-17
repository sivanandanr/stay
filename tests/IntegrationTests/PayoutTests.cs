using Dapper;
using Npgsql;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure;
using Stay.Payment.Infrastructure.Payouts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / §9 — owner payouts via Route: a statement deducts commission per booking, is idempotent
/// per (host, period), and executes against the gateway once (PAID), recording a failure rather than
/// losing it, and emits an audit event either way.
/// </summary>
public sealed class PayoutTests : IAsyncLifetime
{
    private const long HostId = 4242;
    private static readonly DateOnly PeriodStart = new(2030, 6, 1);
    private static readonly DateOnly PeriodEnd = new(2030, 6, 30);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private FakePayoutGateway _gateway = null!;
    private PayoutService _payouts = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _gateway = new FakePayoutGateway();
        _payouts = new PayoutService(_postgres.GetConnectionString(), _gateway);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private GeneratePayoutRequest Request(decimal commissionPct, params decimal[] grosses) =>
        new(HostId, PeriodStart, PeriodEnd, commissionPct, "INR",
            grosses.Select((g, i) => new PayoutLineInput(1000 + i, g)).ToList());

    private async Task<(string Status, string? Ref, int Lines, int Outbox)> StateAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<(string Status, string? Ref)>(
            "SELECT status AS Status, statement_ref AS Ref FROM payment.payout WHERE id = @id", new { id });
        var lines = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM payment.payout_line WHERE payout_id = @id", new { id });
        var outbox = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM payment.outbox_message");
        return (row.Status, row.Ref, lines, outbox);
    }

    [Fact]
    public async Task Generating_deducts_commission_and_writes_a_line_per_booking()
    {
        var result = await _payouts.GenerateAsync(Request(15m, 1000.00m, 500.00m));

        Assert.True(result.IsSuccess);
        var p = result.Value!;
        Assert.Equal(1500.00m, p.GrossAmount);
        Assert.Equal(225.00m, p.Commission);  // 15% of 1500
        Assert.Equal(1275.00m, p.NetAmount);
        Assert.Equal("DRAFT", p.Status);
        Assert.Equal(2, (await StateAsync(p.Id)).Lines);
    }

    [Fact]
    public async Task Generating_twice_for_the_same_host_and_period_is_idempotent()
    {
        var first = await _payouts.GenerateAsync(Request(10m, 800.00m));
        var second = await _payouts.GenerateAsync(Request(10m, 800.00m));

        Assert.Equal(first.Value!.Id, second.Value!.Id);   // same statement returned, not duplicated
        Assert.Equal(1, (await StateAsync(first.Value.Id)).Lines);
    }

    [Fact]
    public async Task Executing_pays_the_net_and_emits_an_audit_event()
    {
        var payout = (await _payouts.GenerateAsync(Request(20m, 1000.00m))).Value!;

        var result = await _payouts.ExecuteAsync(payout.Id, "acc_host_4242", "finance|1");

        Assert.Equal("PAID", result.Value!.Status);
        Assert.NotNull(result.Value.StatementRef);
        var state = await StateAsync(payout.Id);
        Assert.Equal("PAID", state.Status);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task Executing_is_idempotent_and_does_not_pay_or_emit_twice()
    {
        var payout = (await _payouts.GenerateAsync(Request(20m, 1000.00m))).Value!;

        await _payouts.ExecuteAsync(payout.Id, "acc_host_4242", "finance|1");
        var second = await _payouts.ExecuteAsync(payout.Id, "acc_host_4242", "finance|1");

        Assert.Equal("PAID", second.Value!.Status);
        Assert.Equal(1, (await StateAsync(payout.Id)).Outbox); // one event despite two executes
    }

    [Fact]
    public async Task A_failed_payout_is_recorded_not_lost()
    {
        var payout = (await _payouts.GenerateAsync(Request(20m, 1000.00m))).Value!;
        _gateway.FailKey($"stay:payout:{payout.Id}");

        var result = await _payouts.ExecuteAsync(payout.Id, "acc_host_4242", "finance|1");

        Assert.Equal("FAILED", result.Value!.Status);
        Assert.NotNull(result.Value.FailureReason);
        var state = await StateAsync(payout.Id);
        Assert.Equal("FAILED", state.Status);
        Assert.Equal(1, state.Outbox);                        // audit event still emitted
    }

    [Fact]
    public async Task Commission_outside_zero_to_hundred_is_rejected()
    {
        var result = await _payouts.GenerateAsync(Request(150m, 1000.00m));

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
    }
}
