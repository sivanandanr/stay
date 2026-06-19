using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Cancellation;
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

/// <summary>Cancellation + refund (§9): restore inventory BEFORE the refund; a refund failure never un-restores it.</summary>
public sealed class BookingCancellationTests : IAsyncLifetime
{
    private const long RoomTypeId = 7;
    private const long RatePlanId = 3;
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13); // 3 nights × 100 = 300

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

    /// <summary>Holds and confirms a booking (optionally with a frozen cancellation policy), returning its id.</summary>
    private async Task<long> ConfirmedBookingAsync(CancellationSnapshot? snapshot = null)
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
            CheckIn, CheckOut, 1, 2, 0, TimeSpan.FromMinutes(15), snapshot));
        var bookingId = held.Value!.BookingId;
        await new BookingConfirmService(_postgres.GetConnectionString(), new FakePaymentGateway(), new PromotionService(_postgres.GetConnectionString()), new LoyaltyService(_postgres.GetConnectionString())).ConfirmAsync(bookingId);
        return bookingId;
    }

    private CancelBookingService Cancel(IPaymentGateway gateway) => new(_postgres.GetConnectionString(), gateway);

    private async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, p))!;
    }

    [Fact]
    public async Task Cancel_restores_inventory_and_refunds_in_full()
    {
        var bookingId = await ConfirmedBookingAsync();
        Assert.Equal(1, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn }));

        var result = await Cancel(new FakePaymentGateway()).CancelAsync(bookingId, "Guest changed plans.", "GUEST");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(300m, result.Value!.RefundAmount);
        Assert.Equal("CANCELLED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn })); // restored
        Assert.Equal("SUCCEEDED", await ScalarAsync<string>("SELECT status FROM payment.refund WHERE payment_id IN (SELECT id FROM payment.payment WHERE booking_id=@Id)", new { Id = bookingId }));
        Assert.Equal("REFUNDED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM booking.outbox_message WHERE type='stay.booking.cancelled'"));
    }

    [Fact]
    public async Task Partial_refund_marks_the_payment_partially_refunded()
    {
        var bookingId = await ConfirmedBookingAsync();

        var result = await Cancel(new FakePaymentGateway()).CancelAsync(bookingId, "Late cancel.", "OPS", refundPercent: 50);

        Assert.Equal(150m, result.Value!.RefundAmount); // 50% of 300
        Assert.Equal("PARTIALLY_REFUNDED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE booking_id=@Id", new { Id = bookingId }));
    }

    [Fact]
    public async Task Refund_failure_leaves_inventory_restored_and_the_refund_pending()
    {
        var bookingId = await ConfirmedBookingAsync();

        var result = await Cancel(new RefundFailingGateway()).CancelAsync(bookingId, "Guest cancel.", "GUEST");

        Assert.True(result.IsSuccess); // the guest's cancellation isn't blocked by a refund failure
        Assert.Equal("CANCELLED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn })); // restored regardless
        Assert.Equal("PENDING", await ScalarAsync<string>("SELECT status FROM payment.refund WHERE payment_id IN (SELECT id FROM payment.payment WHERE booking_id=@Id)", new { Id = bookingId })); // queued for retry
    }

    [Fact]
    public async Task Cancel_is_idempotent()
    {
        var bookingId = await ConfirmedBookingAsync();

        await Cancel(new FakePaymentGateway()).CancelAsync(bookingId, "First.", "GUEST");
        var second = await Cancel(new FakePaymentGateway()).CancelAsync(bookingId, "Retry.", "GUEST");

        Assert.True(second.IsSuccess);
        Assert.Equal(0, await ScalarAsync<int>("SELECT units_sold FROM ari.inventory_calendar WHERE room_type_id=@RoomTypeId AND stay_date=@d", new { RoomTypeId, d = CheckIn })); // not double-restored
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM booking.cancellation WHERE booking_id=@Id", new { Id = bookingId }));
    }

    [Fact]
    public async Task Cancel_rejects_a_non_confirmed_booking()
    {
        // A merely-held (not confirmed) booking can't be cancelled through this flow.
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

        var result = await Cancel(new FakePaymentGateway()).CancelAsync(held.Value!.BookingId, "x", "GUEST");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid-state", result.Error!.Value.Code);
    }

    private static CancellationContext Policy(bool refundable) => new(
        new CancellationPolicy(refundable, [new CancellationTier(48, 100), new CancellationTier(24, 50)]),
        new TimeOnly(14, 0), "Asia/Singapore");

    [Fact]
    public async Task Cancellation_policy_computes_the_refund_from_the_tiers()
    {
        var bookingId = await ConfirmedBookingAsync(); // check-in is years away → top tier (100%)

        var result = await Cancel(new FakePaymentGateway())
            .CancelAsync(bookingId, "Plans changed.", "GUEST", policyContext: Policy(refundable: true));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(300m, result.Value!.RefundAmount); // evaluated from the policy, not the manual default
    }

    [Fact]
    public async Task Non_refundable_policy_refunds_nothing()
    {
        var bookingId = await ConfirmedBookingAsync();

        var result = await Cancel(new FakePaymentGateway())
            .CancelAsync(bookingId, "Late.", "GUEST", policyContext: Policy(refundable: false));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value!.RefundAmount);
        Assert.Equal("CANCELLED", await ScalarAsync<string>("SELECT status FROM booking.booking WHERE id=@Id", new { Id = bookingId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*) FROM payment.refund WHERE payment_id IN (SELECT id FROM payment.payment WHERE booking_id=@Id)", new { Id = bookingId }));
    }

    [Fact]
    public async Task Cancel_self_resolves_the_policy_frozen_at_hold_time()
    {
        // Hold freezes a refundable policy onto the booking; cancel evaluates it WITHOUT a passed context.
        var snapshot = new CancellationSnapshot("Asia/Singapore", new TimeOnly(14, 0), IsRefundable: true,
            [new CancellationTierDto(48, 100), new CancellationTierDto(24, 50)]);
        var bookingId = await ConfirmedBookingAsync(snapshot);

        var result = await Cancel(new FakePaymentGateway()).CancelAsync(bookingId, "Plans changed.", "GUEST");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(300m, result.Value!.RefundAmount); // check-in is years away → top tier (100%)
    }

    private sealed class RefundFailingGateway : IPaymentGateway
    {
        public Task<OrderResult> CreateOrderAsync(OrderRequest r, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult($"order_{r.IdempotencyKey}", "rzp_test", r.Amount, r.Currency));
        public Task<VerificationResult> VerifyCheckoutAsync(CheckoutProof p, string key, CancellationToken ct = default) =>
            Task.FromResult(VerificationResult.Ok(p.PaymentId));
        public Task<AuthorizationResult> AuthorizeAsync(PaymentInstruction i, CancellationToken ct = default) =>
            Task.FromResult(AuthorizationResult.Approved($"auth_{i.IdempotencyKey}"));
        public Task<CaptureResult> CaptureAsync(string pspRef, string key, CancellationToken ct = default) =>
            Task.FromResult(CaptureResult.Ok());
        public Task<RefundResult> RefundAsync(string pspRef, decimal amount, string key, CancellationToken ct = default) =>
            Task.FromResult(RefundResult.Failed("psp_unavailable"));
        public Task<GatewayPaymentStatus> GetStatusAsync(string pspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayPaymentStatus.Captured);
        public Task<GatewayRefundStatus> GetRefundStatusAsync(string refundPspRef, CancellationToken ct = default) =>
            Task.FromResult(GatewayRefundStatus.Processed);
    }
}
