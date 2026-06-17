using Dapper;
using Npgsql;
using Stay.Payment.Infrastructure.Webhooks;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Webhooks are the source of truth for async PSP state (§9), ingested idempotently by provider event id.</summary>
public sealed class PaymentWebhookTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private PaymentWebhookService _webhooks = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _webhooks = new PaymentWebhookService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a payment row and returns its psp_ref.</summary>
    private async Task<string> SeedPaymentAsync(string status, string? pspRef = null)
    {
        pspRef ??= $"ch_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO payment.payment (booking_id, psp, psp_ref, amount, currency, status, idempotency_key)
            VALUES (1, 'RAZORPAY', @pspRef, 300, 'SGD', @status, @key)
            """, new { pspRef, status, key = Guid.NewGuid().ToString("N") });
        return pspRef;
    }

    private async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, p))!;
    }

    private static PaymentWebhook Event(string type, string pspRef, string? eventId = null) =>
        new("RAZORPAY", eventId ?? Guid.NewGuid().ToString("N"), type, pspRef, "{}");

    [Fact]
    public async Task Captured_webhook_marks_the_payment_captured()
    {
        var pspRef = await SeedPaymentAsync("AUTHORIZED");

        var outcome = await _webhooks.IngestAsync(Event("payment.captured", pspRef));

        Assert.Equal(WebhookOutcome.Processed, outcome);
        Assert.Equal("CAPTURED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE psp_ref=@pspRef", new { pspRef }));
    }

    [Fact]
    public async Task Failed_webhook_marks_the_payment_failed()
    {
        var pspRef = await SeedPaymentAsync("AUTHORIZED");

        await _webhooks.IngestAsync(Event("payment.failed", pspRef));

        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE psp_ref=@pspRef", new { pspRef }));
    }

    [Fact]
    public async Task Refund_processed_webhook_settles_the_refund_and_payment()
    {
        var pspRef = await SeedPaymentAsync("CAPTURED");
        var paymentId = await ScalarAsync<long>("SELECT id FROM payment.payment WHERE psp_ref=@pspRef", new { pspRef });
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO payment.refund (payment_id, amount, currency, status, idempotency_key)
                VALUES (@paymentId, 300, 'SGD', 'PENDING', @key)
                """, new { paymentId, key = Guid.NewGuid().ToString("N") });
        }

        await _webhooks.IngestAsync(Event("refund.processed", pspRef));

        Assert.Equal("SUCCEEDED", await ScalarAsync<string>("SELECT status FROM payment.refund WHERE payment_id=@paymentId", new { paymentId }));
        Assert.Equal("REFUNDED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE id=@paymentId", new { paymentId }));
    }

    [Fact]
    public async Task Duplicate_event_id_is_processed_exactly_once()
    {
        var pspRef = await SeedPaymentAsync("AUTHORIZED");
        var evt = Event("payment.captured", pspRef, eventId: "evt_dup");

        var first = await _webhooks.IngestAsync(evt);
        var second = await _webhooks.IngestAsync(evt);

        Assert.Equal(WebhookOutcome.Processed, first);
        Assert.Equal(WebhookOutcome.Duplicate, second);     // redelivery absorbed
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM payment.webhook_event WHERE psp_event_id='evt_dup'"));
        Assert.Equal("CAPTURED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE psp_ref=@pspRef", new { pspRef }));
    }

    [Fact]
    public async Task Unknown_event_type_is_recorded_but_changes_nothing()
    {
        var pspRef = await SeedPaymentAsync("AUTHORIZED");

        var outcome = await _webhooks.IngestAsync(Event("payment.disputed", pspRef));

        Assert.Equal(WebhookOutcome.Ignored, outcome);
        Assert.Equal("AUTHORIZED", await ScalarAsync<string>("SELECT status FROM payment.payment WHERE psp_ref=@pspRef", new { pspRef })); // unchanged
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM payment.webhook_event WHERE type='payment.disputed'")); // still recorded
    }
}
