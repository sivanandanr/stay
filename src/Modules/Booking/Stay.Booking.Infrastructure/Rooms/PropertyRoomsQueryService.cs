using Dapper;
using Npgsql;

namespace Stay.Booking.Infrastructure.Rooms;

/// <summary>A bookable rate plan offered on a property.</summary>
public sealed record RatePlanSummary(long Id, string Name, string? MealPlan, bool Refundable);

/// <summary>A room type a guest can book, with a lead-in "from" price (min calendar rate).</summary>
public sealed record RoomTypeSummary(
    long RoomTypeId,
    string Name,
    string UnitKind,
    short BaseOccupancy,
    short MaxOccupancy,
    short? MaxAdults,
    short? MaxChildren,
    decimal? FromPrice);

/// <summary>The room types + rate plans a guest can combine to price/hold a stay.</summary>
public sealed record PropertyRooms(long PropertyId, IReadOnlyList<RatePlanSummary> RatePlans, IReadOnlyList<RoomTypeSummary> Rooms);

/// <summary>
/// A guest READ-model query that lists a property's bookable room types and rate plans — the funnel's
/// "choose a room" step. It joins catalog (room types) and ARI (rate plans + calendar) on the read side
/// (CQRS query, not a write coupling): the funnel already lives with ARI under Golden Rule §1.7, and this
/// only reads. The result feeds <c>/availability/quote</c> with a concrete (roomType, ratePlan) pair.
/// </summary>
public sealed class PropertyRoomsQueryService(string connectionString)
{
    public async Task<PropertyRooms> GetRoomsAsync(long propertyId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var ratePlans = (await conn.QueryAsync<RatePlanSummary>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, meal_plan AS MealPlan, is_refundable AS Refundable
            FROM ari.rate_plan
            WHERE property_id = @propertyId AND status = 'ACTIVE'
            ORDER BY id
            """, new { propertyId }, cancellationToken: ct))).AsList();

        var rooms = (await conn.QueryAsync<RoomTypeSummary>(new CommandDefinition("""
            SELECT rt.id AS RoomTypeId, rt.name AS Name, rt.unit_kind AS UnitKind,
                   rt.base_occupancy AS BaseOccupancy, rt.max_occupancy AS MaxOccupancy,
                   rt.max_adults AS MaxAdults, rt.max_children AS MaxChildren,
                   (SELECT MIN(rc.base_price) FROM ari.rate_calendar rc WHERE rc.room_type_id = rt.id) AS FromPrice
            FROM catalog.room_type rt
            WHERE rt.property_id = @propertyId
            ORDER BY rt.id
            """, new { propertyId }, cancellationToken: ct))).AsList();

        return new PropertyRooms(propertyId, ratePlans, rooms);
    }
}
