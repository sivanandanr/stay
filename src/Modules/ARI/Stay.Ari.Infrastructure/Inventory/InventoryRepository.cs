using Dapper;
using Npgsql;

namespace Stay.Ari.Infrastructure.Inventory;

/// <summary>Outcome of an inventory hold attempt over a stay-night range.</summary>
public enum HoldOutcome
{
    Held,
    SoldOut
}

/// <summary>
/// The no-overbooking inventory core (BR-1, Gate G1). Every method takes the caller's open
/// connection and transaction: the booking saga's hold is a single DB transaction across Booking +
/// ARI (+ Pricing), never split (CLAUDE.md Golden Rule §1.7). This is a Dapper hot path (§5) —
/// the hold is one round trip.
/// </summary>
public sealed class InventoryRepository
{
    static InventoryRepository() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    /// <summary>
    /// Upserts <paramref name="totalAllotment"/> for every night in <c>[from, toExclusive)</c>.
    /// Rows must land in an existing monthly partition.
    /// </summary>
    public async Task SetAllotmentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly from, DateOnly toExclusive, int totalAllotment, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO ari.inventory_calendar (room_type_id, stay_date, total_allotment)
            SELECT @roomTypeId, gs::date, @totalAllotment
            FROM generate_series(@from::date, @toExclusive::date - 1, interval '1 day') AS gs
            ON CONFLICT (room_type_id, stay_date) DO UPDATE
                SET total_allotment = EXCLUDED.total_allotment,
                    row_version     = ari.inventory_calendar.row_version + 1
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, from, toExclusive, totalAllotment }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Atomically holds <paramref name="quantity"/> units across every night in
    /// <c>[checkIn, checkOut)</c> via ONE conditional UPDATE (BR-1). Postgres row-locks each night,
    /// so concurrent holds can't oversell. All-nights-or-none: if the affected row count is less than
    /// the night count, some night lacked availability — the caller must roll back to undo any
    /// partial hold (the row count is checked here; the transaction boundary is the caller's).
    /// </summary>
    public async Task<HoldOutcome> TryHoldAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly checkIn, DateOnly checkOut, int quantity, CancellationToken ct = default)
    {
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        if (nights <= 0)
            return HoldOutcome.SoldOut;

        const string sql = """
            UPDATE ari.inventory_calendar
            SET units_held  = units_held + @quantity,
                row_version = row_version + 1
            WHERE room_type_id = @roomTypeId
              AND stay_date >= @checkIn
              AND stay_date <  @checkOut
              AND stop_sell = false
              AND (total_allotment - units_sold - units_held) >= @quantity
            """;

        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, checkIn, checkOut, quantity }, tx, cancellationToken: ct));

        return affected == nights ? HoldOutcome.Held : HoldOutcome.SoldOut;
    }

    /// <summary>
    /// Commits a held quantity to sold across the night range (units_held → units_sold) on confirm.
    /// The BR-1 invariant (sold + held ≤ allotment) is preserved — units only move between counters.
    /// </summary>
    public async Task<int> ConfirmAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly checkIn, DateOnly checkOut, int quantity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE ari.inventory_calendar
            SET units_held  = units_held - @quantity,
                units_sold  = units_sold + @quantity,
                row_version = row_version + 1
            WHERE room_type_id = @roomTypeId
              AND stay_date >= @checkIn
              AND stay_date <  @checkOut
              AND units_held >= @quantity
            """;

        return await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, checkIn, checkOut, quantity }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Restores sold inventory back to availability across the night range (units_sold down) when a
    /// confirmed booking is cancelled. Called BEFORE issuing the refund (§9).
    /// </summary>
    public async Task ReleaseConfirmedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly checkIn, DateOnly checkOut, int quantity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE ari.inventory_calendar
            SET units_sold  = units_sold - @quantity,
                row_version = row_version + 1
            WHERE room_type_id = @roomTypeId
              AND stay_date >= @checkIn
              AND stay_date <  @checkOut
              AND units_sold >= @quantity
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, checkIn, checkOut, quantity }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Directly sells a quantity across the night range (conditional units_sold += qty, all-nights-or-none)
    /// — used when modifying a confirmed booking onto new dates. Returns <see cref="HoldOutcome.Held"/>
    /// on success; the caller rolls back on <see cref="HoldOutcome.SoldOut"/>. BR-1 invariant holds.
    /// </summary>
    public async Task<HoldOutcome> TrySellAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly checkIn, DateOnly checkOut, int quantity, CancellationToken ct = default)
    {
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        if (nights <= 0)
            return HoldOutcome.SoldOut;

        const string sql = """
            UPDATE ari.inventory_calendar
            SET units_sold  = units_sold + @quantity,
                row_version = row_version + 1
            WHERE room_type_id = @roomTypeId
              AND stay_date >= @checkIn
              AND stay_date <  @checkOut
              AND stop_sell = false
              AND (total_allotment - units_sold - units_held) >= @quantity
            """;

        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, checkIn, checkOut, quantity }, tx, cancellationToken: ct));

        return affected == nights ? HoldOutcome.Held : HoldOutcome.SoldOut;
    }

    /// <summary>Releases a previously held quantity across the night range (e.g. hold expiry/cancel).</summary>
    public async Task ReleaseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long roomTypeId, DateOnly checkIn, DateOnly checkOut, int quantity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE ari.inventory_calendar
            SET units_held  = units_held - @quantity,
                row_version = row_version + 1
            WHERE room_type_id = @roomTypeId
              AND stay_date >= @checkIn
              AND stay_date <  @checkOut
              AND units_held >= @quantity
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { roomTypeId, checkIn, checkOut, quantity }, tx, cancellationToken: ct));
    }
}
