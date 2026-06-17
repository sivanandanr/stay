using Dapper;
using Npgsql;

namespace Stay.Booking.Infrastructure.Reporting;

/// <summary>A count of bookings in one status within the report window.</summary>
public sealed record StatusCount(string Status, int Count);

/// <summary>Realized (CONFIRMED) revenue for one currency within the window — money stays per-currency, never summed across (§5).</summary>
public sealed record RevenueLine(string Currency, decimal ConfirmedRevenue);

/// <summary>An ops/finance booking summary for a date window.</summary>
public sealed record BookingReport(int TotalBookings, IReadOnlyList<StatusCount> ByStatus, IReadOnlyList<RevenueLine> Revenue);

/// <summary>
/// Read-only ops/finance reporting over the booking ledger (Phase 8). Aggregations are projected from
/// exact columns (no N+1, §11) and bounded to a date window. Revenue is reported per currency and only
/// for CONFIRMED bookings — realized money — never blended across currencies.
/// </summary>
public sealed class ReportingService(string connectionString)
{
    public async Task<BookingReport> BookingSummaryAsync(DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var byStatus = (await conn.QueryAsync<StatusCount>(new CommandDefinition("""
            SELECT status AS Status, count(*)::int AS Count
            FROM booking.booking
            WHERE created_at >= @from AND created_at < @toExclusive
            GROUP BY status
            ORDER BY status
            """, new { from, toExclusive }, cancellationToken: ct))).AsList();

        var revenue = (await conn.QueryAsync<RevenueLine>(new CommandDefinition("""
            SELECT currency AS Currency, COALESCE(SUM(total_amount), 0) AS ConfirmedRevenue
            FROM booking.booking
            WHERE status = 'CONFIRMED' AND created_at >= @from AND created_at < @toExclusive
            GROUP BY currency
            ORDER BY currency
            """, new { from, toExclusive }, cancellationToken: ct))).AsList();

        return new BookingReport(byStatus.Sum(s => s.Count), byStatus, revenue);
    }
}
