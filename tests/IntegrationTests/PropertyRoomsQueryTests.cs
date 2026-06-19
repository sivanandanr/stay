using Dapper;
using Npgsql;
using Stay.Booking.Infrastructure.Rooms;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The funnel's "choose a room" read model: a property's bookable room types + rate plans, with a
/// lead-in "from" price (min calendar rate). A guest read-side join over catalog + ARI.
/// </summary>
public sealed class PropertyRoomsQueryTests : IAsyncLifetime
{
    private const long PropertyId = 99;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private PropertyRoomsQueryService _rooms = null!;

    private const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS catalog;
        CREATE SCHEMA IF NOT EXISTS ari;

        CREATE TABLE catalog.room_type (
            id BIGINT PRIMARY KEY, property_id BIGINT NOT NULL, name TEXT NOT NULL, unit_kind TEXT NOT NULL,
            base_occupancy SMALLINT NOT NULL, max_occupancy SMALLINT NOT NULL, max_adults SMALLINT, max_children SMALLINT
        );
        CREATE TABLE ari.rate_plan (
            id BIGINT PRIMARY KEY, property_id BIGINT NOT NULL, name TEXT NOT NULL, meal_plan TEXT,
            is_refundable BOOLEAN NOT NULL DEFAULT true, status TEXT NOT NULL DEFAULT 'ACTIVE'
        );
        CREATE TABLE ari.rate_calendar (
            room_type_id BIGINT NOT NULL, rate_plan_id BIGINT NOT NULL, stay_date DATE NOT NULL,
            base_price NUMERIC(12,2) NOT NULL, currency CHAR(3) NOT NULL
        );
        """;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(Ddl);
        _rooms = new PropertyRoomsQueryService(_postgres.GetConnectionString());
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO catalog.room_type (id, property_id, name, unit_kind, base_occupancy, max_occupancy, max_adults, max_children) VALUES
                (1, @p, 'Deluxe Room', 'ROOM', 2, 3, 3, 1),
                (2, @p, 'Entire Villa', 'ENTIRE_UNIT', 4, 8, 8, 4),
                (3, 12345, 'Other Property Room', 'ROOM', 2, 2, 2, 0);
            INSERT INTO ari.rate_plan (id, property_id, name, meal_plan, is_refundable, status) VALUES
                (10, @p, 'Standard', 'ROOM_ONLY', true, 'ACTIVE'),
                (11, @p, 'With Breakfast', 'BREAKFAST', true, 'ACTIVE'),
                (12, @p, 'Old Plan', 'ROOM_ONLY', true, 'INACTIVE');
            INSERT INTO ari.rate_calendar (room_type_id, rate_plan_id, stay_date, base_price, currency) VALUES
                (1, 10, DATE '2030-06-10', 2500, 'INR'),
                (1, 10, DATE '2030-06-11', 2200, 'INR');
            """, new { p = PropertyId }); // room 2 deliberately has no calendar rates
    }

    [Fact]
    public async Task Returns_room_types_with_lead_in_price_and_active_rate_plans()
    {
        await SeedAsync();

        var result = await _rooms.GetRoomsAsync(PropertyId);

        // Only this property's rooms (id 3 belongs to another property).
        Assert.Equal(2, result.Rooms.Count);
        Assert.Equal(new long[] { 1, 2 }, result.Rooms.Select(r => r.RoomTypeId).ToArray());

        var deluxe = result.Rooms[0];
        Assert.Equal("Deluxe Room", deluxe.Name);
        Assert.Equal(3, deluxe.MaxOccupancy);
        Assert.Equal(2200m, deluxe.FromPrice); // min calendar rate

        // Only ACTIVE rate plans (id 12 is INACTIVE).
        Assert.Equal(2, result.RatePlans.Count);
        Assert.Equal(new long[] { 10, 11 }, result.RatePlans.Select(p => p.Id).ToArray());
        Assert.Equal("BREAKFAST", result.RatePlans[1].MealPlan);
    }

    [Fact]
    public async Task A_room_without_calendar_rates_has_a_null_from_price()
    {
        await SeedAsync();

        var result = await _rooms.GetRoomsAsync(PropertyId);

        Assert.Null(result.Rooms.Single(r => r.RoomTypeId == 2).FromPrice); // Entire Villa has no rates seeded
    }

    [Fact]
    public async Task An_unknown_property_returns_empty()
    {
        var result = await _rooms.GetRoomsAsync(404);

        Assert.Empty(result.Rooms);
        Assert.Empty(result.RatePlans);
    }
}
