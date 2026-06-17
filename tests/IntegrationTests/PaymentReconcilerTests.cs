using Dapper;
using Npgsql;
using Stay.Payment.Contracts;
using Stay.Payment.Infrastructure;
using Stay.Payment.Infrastructure.Reconciliation;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>The payment poll-reconciler (§9): settles payments stuck non-terminal past a threshold, via the gateway.</summary>
public sealed class PaymentReconcilerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private PaymentReconciler _reconciler = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(PaymentSchema.Ddl);
        _reconciler = new PaymentReconciler(_postgres.GetConnectionString(), new FakePaymentGateway());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a payment whose updated_at is <paramref name="ageMinutes"/> minutes in the past.</summary>
    private async Task<long> SeedPaymentAsync(string status, int ageMinutes, string? pspRef = "ch_1")
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO payment.payment (booking_id, psp, psp_ref, amount, currency, status, idempotency_key, updated_at)
            VALUES (1, 'RAZORPAY', @pspRef, 300, 'SGD', @status, @key, now() - make_interval(mins => @ageMinutes))
            RETURNING id
            """, new { pspRef, status, key = Guid.NewGuid().ToString("N"), ageMinutes });
    }

    private async Task<string> StatusAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<string>("SELECT status FROM payment.payment WHERE id=@id", new { id }))!;
    }

    [Fact]
    public async Task A_stale_authorized_payment_is_settled_from_the_gateway()
    {
        var id = await SeedPaymentAsync("AUTHORIZED", ageMinutes: 60);

        var reconciled = await _reconciler.ReconcileAsync(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);

        Assert.Equal(1, reconciled);
        Assert.Equal("CAPTURED", await StatusAsync(id)); // gateway reports captured
    }

    [Fact]
    public async Task A_recent_payment_is_left_alone()
    {
        var id = await SeedPaymentAsync("AUTHORIZED", ageMinutes: 5); // within the 30-min window

        var reconciled = await _reconciler.ReconcileAsync(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);

        Assert.Equal(0, reconciled);
        Assert.Equal("AUTHORIZED", await StatusAsync(id));
    }

    [Fact]
    public async Task Terminal_payments_are_not_touched()
    {
        var id = await SeedPaymentAsync("CAPTURED", ageMinutes: 120);

        Assert.Equal(0, await _reconciler.ReconcileAsync(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow));
        Assert.Equal("CAPTURED", await StatusAsync(id));
    }

    [Fact]
    public async Task Reconciliation_is_idempotent()
    {
        await SeedPaymentAsync("AUTHORIZED", ageMinutes: 60);

        var first = await _reconciler.ReconcileAsync(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
        var second = await _reconciler.ReconcileAsync(TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // already settled → nothing to do
    }
}
