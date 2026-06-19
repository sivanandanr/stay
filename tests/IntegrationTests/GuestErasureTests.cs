using Dapper;
using Npgsql;
using Stay.Guest.Infrastructure;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Right-to-erasure (BR-8): the guest's master PII is anonymized in one transaction — profile cache
/// nulled, identity severed to a tombstone, saved travelers + payment tokens deleted — and a
/// GuestErased event is emitted for the audit log + booking anonymization. Idempotent (BR-5).
/// </summary>
public sealed class GuestErasureTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private GuestErasureService _erasure = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(GuestSchema.Ddl);
        _erasure = new GuestErasureService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>Seeds a profile with one saved traveler and one payment token; returns the guest id.</summary>
    private async Task<long> SeedGuestAsync(string sub = "user|42")
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO guest.guest_profile (identity_sub, email_cache, name_cache, email_verified_cache, locale)
            VALUES (@sub, 'jane@example.com', 'Jane Doe', true, 'en-IN')
            RETURNING id
            """, new { sub });
        await conn.ExecuteAsync(
            "INSERT INTO guest.saved_traveler (guest_id, full_name, nationality) VALUES (@id, 'Jane Doe', 'IN')",
            new { id });
        await conn.ExecuteAsync(
            "INSERT INTO guest.payment_method_token (guest_id, psp, token, last4) VALUES (@id, 'RAZORPAY', 'tok_x', '4242')",
            new { id });
        return id;
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task Erase_anonymizes_the_profile_and_removes_personal_data()
    {
        var guestId = await SeedGuestAsync();

        var result = await _erasure.EraseAsync(guestId, "user|42");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.AlreadyErased);
        Assert.Equal(1, result.Value!.TravelersDeleted);
        Assert.Equal(1, result.Value!.PaymentTokensDeleted);

        // PII gone; the row remains but anonymized + tombstoned.
        Assert.Null(await ScalarAsync<string?>(
            "SELECT email_cache FROM guest.guest_profile WHERE id = @guestId", new { guestId }));
        Assert.Null(await ScalarAsync<string?>(
            "SELECT name_cache FROM guest.guest_profile WHERE id = @guestId", new { guestId }));
        Assert.Equal($"erased:{guestId}", await ScalarAsync<string>(
            "SELECT identity_sub FROM guest.guest_profile WHERE id = @guestId", new { guestId }));
        Assert.NotNull(await ScalarAsync<DateTime?>(
            "SELECT erased_at FROM guest.guest_profile WHERE id = @guestId", new { guestId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*)::int FROM guest.saved_traveler WHERE guest_id = @guestId", new { guestId }));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*)::int FROM guest.payment_method_token WHERE guest_id = @guestId", new { guestId }));
    }

    [Fact]
    public async Task Erase_emits_a_GuestErased_event_to_the_outbox()
    {
        var guestId = await SeedGuestAsync();

        await _erasure.EraseAsync(guestId, "user|42");

        Assert.Equal("stay.guest.erased", await ScalarAsync<string>(
            "SELECT type FROM guest.outbox_message", new { }));
        Assert.Contains(guestId.ToString(), await ScalarAsync<string>(
            "SELECT payload::text FROM guest.outbox_message", new { }));
    }

    [Fact]
    public async Task Erase_is_idempotent_and_does_not_re_emit()
    {
        var guestId = await SeedGuestAsync();

        await _erasure.EraseAsync(guestId, "user|42");
        var second = await _erasure.EraseAsync(guestId, "user|42"); // replay

        Assert.True(second.IsSuccess);
        Assert.True(second.Value!.AlreadyErased);
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM guest.outbox_message", new { })); // not re-emitted
    }

    [Fact]
    public async Task Erasing_an_unknown_guest_is_not_found()
    {
        var result = await _erasure.EraseAsync(999_999, "ops|1");

        Assert.False(result.IsSuccess);
        Assert.Equal("guest-not-found", result.Error!.Value.Code);
    }
}
