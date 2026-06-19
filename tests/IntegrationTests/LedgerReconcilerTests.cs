using Dapper;
using Npgsql;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure.Reconciliation;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / Gate G2 — daily ledger reconciliation reports every divergence between local money state
/// (payments, refunds, payouts) and the PSP, and stays read-only (it flags, it doesn't settle).
/// Balanced ⇔ zero deltas.
/// </summary>
public sealed class LedgerReconcilerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly ConfigurableGateway _gateway = new();
    private readonly ConfigurablePayoutGateway _payoutGateway = new();
    private LedgerReconciler _reconciler = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _reconciler = new LedgerReconciler(_postgres.GetConnectionString(), _gateway, _payoutGateway);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<long> SeedPaymentAsync(string pspRef, string localStatus, GatewayPaymentStatus pspStatus)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO payment.payment (booking_id, psp, psp_ref, amount, currency, status, idempotency_key)
            VALUES (1, 'razorpay', @pspRef, 100.00, 'INR', @localStatus, @pspRef) RETURNING id
            """, new { pspRef, localStatus });
        _gateway.Set(pspRef, pspStatus);
        return id;
    }

    private async Task SeedRefundAsync(string pspRef, GatewayRefundStatus pspStatus)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var paymentId = await SeedPaymentAsync($"pay_{pspRef}", "CAPTURED", GatewayPaymentStatus.Captured);
        await conn.ExecuteAsync("""
            INSERT INTO payment.refund (payment_id, amount, currency, status, psp_ref, idempotency_key)
            VALUES (@paymentId, 50.00, 'INR', 'SUCCEEDED', @pspRef, @pspRef)
            """, new { paymentId, pspRef });
        _gateway.SetRefund(pspRef, pspStatus);
    }

    private async Task SeedPayoutAsync(string statementRef, GatewayPayoutStatus pspStatus)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO payment.payout (host_id, period_start, period_end, gross_amount, commission, net_amount, currency, status, statement_ref)
            VALUES (1, '2030-06-01', '2030-06-30', 100, 10, 90, 'INR', 'PAID', @statementRef)
            """, new { statementRef });
        _payoutGateway.Set(statementRef, pspStatus);
    }

    private Task<ReconciliationReport> ReconcileAsync() =>
        _reconciler.ReconcileAsync(DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public async Task A_fully_matching_ledger_is_balanced()
    {
        await SeedPaymentAsync("p1", "CAPTURED", GatewayPaymentStatus.Captured);
        await SeedRefundAsync("r1", GatewayRefundStatus.Processed);
        await SeedPayoutAsync("po1", GatewayPayoutStatus.Paid);

        var report = await ReconcileAsync();

        Assert.True(report.Balanced);
    }

    [Fact]
    public async Task A_locally_captured_payment_the_psp_did_not_capture_is_flagged()
    {
        await SeedPaymentAsync("p1", "CAPTURED", GatewayPaymentStatus.Failed);

        var report = await ReconcileAsync();

        var delta = Assert.Single(report.Deltas);
        Assert.Equal("payment", delta.EntityType);
        Assert.Equal("local-captured-psp-not", delta.Kind);
        Assert.Equal("p1", delta.Ref);
    }

    [Fact]
    public async Task A_refund_the_psp_did_not_process_is_flagged()
    {
        await SeedRefundAsync("r1", GatewayRefundStatus.Failed);

        var report = await ReconcileAsync();

        var delta = Assert.Single(report.Deltas, d => d.EntityType == "refund");
        Assert.Equal("local-refunded-psp-not", delta.Kind);
        Assert.Equal("r1", delta.Ref);
    }

    [Fact]
    public async Task A_payout_the_psp_did_not_pay_is_flagged()
    {
        await SeedPayoutAsync("po1", GatewayPayoutStatus.Failed);

        var report = await ReconcileAsync();

        var delta = Assert.Single(report.Deltas, d => d.EntityType == "payout");
        Assert.Equal("local-paid-psp-not", delta.Kind);
        Assert.Equal("po1", delta.Ref);
    }

    [Fact]
    public async Task Only_the_diverging_records_are_reported()
    {
        await SeedPaymentAsync("ok", "CAPTURED", GatewayPaymentStatus.Captured);
        await SeedPaymentAsync("bad", "CAPTURED", GatewayPaymentStatus.Pending);
        await SeedRefundAsync("rok", GatewayRefundStatus.Processed);
        await SeedPayoutAsync("pook", GatewayPayoutStatus.Paid);

        var report = await ReconcileAsync();

        Assert.Equal("bad", Assert.Single(report.Deltas).Ref);
    }

    private sealed class ConfigurableGateway : IPaymentGateway
    {
        private readonly Dictionary<string, GatewayPaymentStatus> _payments = [];
        private readonly Dictionary<string, GatewayRefundStatus> _refunds = [];

        public void Set(string pspRef, GatewayPaymentStatus status) => _payments[pspRef] = status;
        public void SetRefund(string pspRef, GatewayRefundStatus status) => _refunds[pspRef] = status;

        public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
            Task.FromResult(_payments.GetValueOrDefault(pspRef, GatewayPaymentStatus.Unknown));
        public Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default) =>
            Task.FromResult(_refunds.GetValueOrDefault(refundPspRef, GatewayRefundStatus.Unknown));

        public Task<OrderResult> CreateOrderAsync(OrderRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof proof, string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ConfigurablePayoutGateway : IPayoutGateway
    {
        private readonly Dictionary<string, GatewayPayoutStatus> _statuses = [];

        public void Set(string pspRef, GatewayPayoutStatus status) => _statuses[pspRef] = status;

        public Task<GatewayPayoutStatus> GetPayoutStatusAsync(string payoutPspRef, CancellationToken ct = default) =>
            Task.FromResult(_statuses.GetValueOrDefault(payoutPspRef, GatewayPayoutStatus.Unknown));

        public Task<PayoutResult> PayoutAsync(PayoutInstruction instruction, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
