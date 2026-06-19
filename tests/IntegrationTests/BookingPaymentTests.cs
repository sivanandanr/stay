using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Stay.Payment.Contracts;
using Stay.Loyalty.Infrastructure;
using Stay.Payment.Infrastructure;
using Stay.Promotion.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Payment integration in confirm (§9): authorize → commit + capture, with the guest never blocked on capture.</summary>
public sealed class BookingPaymentTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();
    private BookingHoldService _hold = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(AriSchema.Ddl);
        await conn.ExecuteAsync(BookingSchema.Ddl);
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _hold = new BookingHoldService(_postgres.GetConnectionString(), new PromotionService(_postgres.GetConnectionString()), new LoyaltyService(_postgres.GetConnectionString()));
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task<long> SeedHeldBookingAsync()
    {
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await _inventory.SetAllotmentAsync(conn, tx, RoomTypeId, CheckIn, CheckOut, 5);
            await _rates.SetRateAsync(conn, tx, RoomTypeId, RatePlanId, CheckIn, CheckOut, 100m, "SGD");
            await tx.CommitAsync();
        }

        var held = await _hold.HoldAsync(new HoldRequest(
            Guid.NewGuid().ToString("N"), 1, "g@example.com", 99, RoomTypeId, RatePlanId,
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15)));
        return held.Value!.BookingId;
    }

    private BookingConfirmService Confirm(IPaymentGateway gateway) =>
        new(_postgres.GetConnectionString(), gateway, new PromotionService(_postgres.GetConnectionString()), new LoyaltyService(_postgres.GetConnectionString()));

    private async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, p))!;
    }

    [Fact]
    public async Task Successful_payment_confirms_and_captures()
    {
        var bookingId = await SeedHeldBookingAsync();

        var result = await Confirm(new FakePaymentGateway()).ConfirmAsync(bookingId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("CONFIRMED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal("CAPTURED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
        Assert.Equal(300m, await ScalarAsync<decimal>("SELECT amount FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
    }

    [Fact]
    public async Task Verified_checkout_proof_confirms_and_records_the_payment_id()
    {
        var bookingId = await SeedHeldBookingAsync();
        var proof = new CheckoutProof("order_1", "pay_xyz", "sig_valid");

        var result = await Confirm(new FakePaymentGateway()).ConfirmAsync(bookingId, proof: proof);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("CONFIRMED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal("CAPTURED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
        // The verified PSP ref recorded is the Razorpay payment id, not a server-side auth ref.
        Assert.Equal("pay_xyz", await ScalarAsync<string>("SELECT psp_ref FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
    }

    [Fact]
    public async Task Invalid_checkout_signature_is_rejected_and_nothing_is_committed()
    {
        var bookingId = await SeedHeldBookingAsync();
        var proof = new CheckoutProof("order_1", "pay_xyz", "invalid"); // the fake rejects "invalid"

        var result = await Confirm(new FakePaymentGateway()).ConfirmAsync(bookingId, proof: proof);

        Assert.False(result.IsSuccess);
        Assert.Equal("payment-verification-failed", result.Error!.Value.Code);
        Assert.Equal("HELD", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM payment.payment"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
    }

    [Fact]
    public async Task Payment_order_for_a_held_booking_returns_checkout_params()
    {
        var bookingId = await SeedHeldBookingAsync();

        var result = await Confirm(new FakePaymentGateway()).CreatePaymentOrderAsync(bookingId, requireGuestId: 1);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.PspOrderId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.KeyId));
        Assert.Equal(300m, result.Value.Amount); // the frozen total (BR-2)
        Assert.Equal("SGD", result.Value.Currency);
        // No DB write — the order is owned by the PSP until confirm.
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM payment.payment"));
        Assert.Equal("HELD", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
    }

    [Fact]
    public async Task Payment_order_for_another_guests_booking_is_not_found()
    {
        var bookingId = await SeedHeldBookingAsync(); // guest 1

        var result = await Confirm(new FakePaymentGateway()).CreatePaymentOrderAsync(bookingId, requireGuestId: 999);

        Assert.False(result.IsSuccess);
        Assert.Equal("booking-not-found", result.Error!.Value.Code); // tenancy: don't leak existence
    }

    [Fact]
    public async Task Declined_authorization_leaves_the_booking_held_and_uncommitted()
    {
        var bookingId = await SeedHeldBookingAsync();

        var result = await Confirm(new DecliningGateway()).ConfirmAsync(bookingId);

        Assert.False(result.IsSuccess);
        Assert.Equal("payment-declined", result.Error!.Value.Code);
        Assert.Equal("HELD", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM payment.payment"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
    }

    [Fact]
    public async Task Capture_failure_after_auth_still_confirms_and_leaves_payment_authorized()
    {
        var bookingId = await SeedHeldBookingAsync();

        // Capture fails after a successful authorization — the guest must not be blocked (§9).
        var result = await Confirm(new CaptureFailingGateway()).ConfirmAsync(bookingId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("CONFIRMED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal("AUTHORIZED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId })); // finance retry
        Assert.Equal(1, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));
    }

    private sealed class DecliningGateway : IPaymentGateway
    {
        public Task<OrderResult> CreateOrderAsync(OrderRequest r, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult($"order_{r.IdempotencyKey}", "rzp_test", r.Amount, r.Currency));
        public Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof p, string key, CancellationToken ct = default) =>
            Task.FromResult(VerificationResult.Ok(p.PaymentId));
        public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction i, CancellationToken ct = default) =>
            Task.FromResult(AuthorizationResult.Declined("insufficient_funds"));
        public Task<CaptureResult> CaptureAsync(string pspRef, string key, CancellationToken ct = default) =>
            Task.FromResult(CaptureResult.Ok());
        public Task<RefundResult> RefundAsync(string pspRef, decimal amount, string key, CancellationToken ct = default) =>
            Task.FromResult(RefundResult.Ok($"refund_{key}"));
        public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayPaymentStatus.Captured);
        public Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayRefundStatus.Processed);
    }

    private sealed class CaptureFailingGateway : IPaymentGateway
    {
        public Task<OrderResult> CreateOrderAsync(OrderRequest r, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult($"order_{r.IdempotencyKey}", "rzp_test", r.Amount, r.Currency));
        public Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof p, string key, CancellationToken ct = default) =>
            Task.FromResult(VerificationResult.Ok(p.PaymentId));
        public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction i, CancellationToken ct = default) =>
            Task.FromResult(AuthorizationResult.Approved($"auth_{i.IdempotencyKey}"));
        public Task<CaptureResult> CaptureAsync(string pspRef, string key, CancellationToken ct = default) =>
            Task.FromResult(CaptureResult.Failed("capture_timeout"));
        public Task<RefundResult> RefundAsync(string pspRef, decimal amount, string key, CancellationToken ct = default) =>
            Task.FromResult(RefundResult.Ok($"refund_{key}"));
        public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayPaymentStatus.Captured);
        public Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayRefundStatus.Processed);
    }
}
