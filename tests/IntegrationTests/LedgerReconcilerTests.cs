using Dapper;
using Npgsql;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure.Reconciliation;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 / Gate G2 — daily ledger reconciliation reports every divergence between local payment
/// state and the PSP, and stays read-only (it flags, it doesn't settle). Balanced ⇔ zero deltas.
/// </summary>
public sealed class LedgerReconcilerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly ConfigurableGateway _gateway = new();
    private LedgerReconciler _reconciler = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _reconciler = new LedgerReconciler(_postgres.GetConnectionString(), _gateway);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a local payment with a status + psp_ref, and the PSP's reported status for it.</summary>
    private async Task SeedAsync(string pspRef, string localStatus, GatewayPaymentStatus pspStatus)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO payment.payment (booking_id, psp, psp_ref, amount, currency, status, idempotency_key)
            VALUES (1, 'razorpay', @pspRef, 100.00, 'INR', @localStatus, @pspRef)
            """, new { pspRef, localStatus });
        _gateway.Set(pspRef, pspStatus);
    }

    private Task<ReconciliationReport> ReconcileAsync() =>
        _reconciler.ReconcileAsync(DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public async Task A_fully_matching_ledger_is_balanced()
    {
        await SeedAsync("p1", "CAPTURED", GatewayPaymentStatus.Captured);
        await SeedAsync("p2", "AUTHORIZED", GatewayPaymentStatus.Authorized);

        var report = await ReconcileAsync();

        Assert.Equal(2, report.Checked);
        Assert.True(report.Balanced);
    }

    [Fact]
    public async Task A_locally_captured_payment_the_psp_did_not_capture_is_flagged()
    {
        await SeedAsync("p1", "CAPTURED", GatewayPaymentStatus.Failed);

        var report = await ReconcileAsync();

        Assert.False(report.Balanced);
        var delta = Assert.Single(report.Deltas);
        Assert.Equal("local-captured-psp-not", delta.Kind);
        Assert.Equal("CAPTURED", delta.LocalStatus);
    }

    [Fact]
    public async Task A_psp_capture_we_never_recorded_is_flagged()
    {
        await SeedAsync("p1", "AUTHORIZED", GatewayPaymentStatus.Captured);

        var report = await ReconcileAsync();

        var delta = Assert.Single(report.Deltas);
        Assert.Equal("psp-captured-local-not", delta.Kind);
    }

    [Fact]
    public async Task An_unknown_psp_status_is_flagged()
    {
        await SeedAsync("p1", "AUTHORIZED", GatewayPaymentStatus.Unknown);

        var report = await ReconcileAsync();

        Assert.Equal("psp-unknown", Assert.Single(report.Deltas).Kind);
    }

    [Fact]
    public async Task Only_the_diverging_payments_are_reported()
    {
        await SeedAsync("ok", "CAPTURED", GatewayPaymentStatus.Captured);
        await SeedAsync("bad", "CAPTURED", GatewayPaymentStatus.Pending);

        var report = await ReconcileAsync();

        Assert.Equal(2, report.Checked);
        Assert.Equal("bad", Assert.Single(report.Deltas).PspRef);
    }

    private sealed class ConfigurableGateway : IPaymentGateway
    {
        private readonly Dictionary<string, GatewayPaymentStatus> _statuses = [];

        public void Set(string pspRef, GatewayPaymentStatus status) => _statuses[pspRef] = status;

        public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
            Task.FromResult(_statuses.GetValueOrDefault(pspRef, GatewayPaymentStatus.Unknown));

        public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction instruction, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CaptureResult> CaptureAsync(string pspRef, string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RefundResult> RefundAsync(string capturedPspRef, decimal amount, string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
