using Dapper;
using Npgsql;
using Stay.Booking.Infrastructure.Reporting;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// Phase 8 — ops reporting summarizes bookings by status and realized (CONFIRMED) revenue per currency
/// within a date window, never blending currencies and respecting the window bounds.
/// </summary>
public sealed class ReportingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset WindowStart = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2030, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private ReportingService _reporting = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(BookingSchema.Ddl);
        _reporting = new ReportingService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAsync(string status, string currency, decimal total, DateTimeOffset createdAt)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO booking.booking (reference, idempotency_key, guest_id, contact_email, property_id, status, currency, total_amount, created_at)
            VALUES ('BK-' || gen_random_uuid(), gen_random_uuid()::text, 1, 'g@x.io', 55, @status, @currency, @total, @createdAt)
            """, new { status, currency, total, createdAt });
    }

    [Fact]
    public async Task Summary_counts_by_status_and_sums_confirmed_revenue_per_currency()
    {
        await SeedAsync("CONFIRMED", "INR", 1000m, WindowStart.AddDays(1));
        await SeedAsync("CONFIRMED", "INR", 500m, WindowStart.AddDays(2));
        await SeedAsync("CONFIRMED", "USD", 200m, WindowStart.AddDays(3));
        await SeedAsync("CANCELLED", "INR", 9999m, WindowStart.AddDays(4)); // not counted as revenue

        var report = await _reporting.BookingSummaryAsync(WindowStart, WindowEnd);

        Assert.Equal(4, report.TotalBookings);
        Assert.Equal(3, report.ByStatus.Single(s => s.Status == "CONFIRMED").Count);
        Assert.Equal(1, report.ByStatus.Single(s => s.Status == "CANCELLED").Count);
        Assert.Equal(1500m, report.Revenue.Single(r => r.Currency == "INR").ConfirmedRevenue);
        Assert.Equal(200m, report.Revenue.Single(r => r.Currency == "USD").ConfirmedRevenue);
    }

    [Fact]
    public async Task Bookings_outside_the_window_are_excluded()
    {
        await SeedAsync("CONFIRMED", "INR", 1000m, WindowStart.AddDays(5));   // inside
        await SeedAsync("CONFIRMED", "INR", 1000m, WindowEnd.AddDays(1));     // after
        await SeedAsync("CONFIRMED", "INR", 1000m, WindowStart.AddDays(-1));  // before

        var report = await _reporting.BookingSummaryAsync(WindowStart, WindowEnd);

        Assert.Equal(1, report.TotalBookings);
        Assert.Equal(1000m, report.Revenue.Single().ConfirmedRevenue);
    }

    [Fact]
    public async Task An_empty_window_reports_zero()
    {
        var report = await _reporting.BookingSummaryAsync(WindowStart, WindowEnd);

        Assert.Equal(0, report.TotalBookings);
        Assert.Empty(report.Revenue);
    }
}
