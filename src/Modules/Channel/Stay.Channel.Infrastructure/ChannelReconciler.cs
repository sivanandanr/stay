using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.BuildingBlocks;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Reconciliation (Phase 7, Gate G5): per connection + mapped room, diff our true availability
/// (<c>total_allotment - units_sold - units_held</c>) against what the channel reports, and open a
/// <c>channel.sync_conflict</c> when the channel shows MORE than we can honor — an oversell risk
/// (BR-1). Idempotent: an OPEN conflict for the same room-type+date isn't duplicated, so the run can
/// be scheduled repeatedly. Resolution (audited) is a separate, ops-driven step.
/// </summary>
public sealed class ChannelReconciler(string connectionString, IChannelClient client)
{
    static ChannelReconciler() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    public async Task<Result<ReconciliationResult>> ReconcileAsync(
        long channelConnectionId, DateOnly from, DateOnly toExclusive, CancellationToken ct = default)
    {
        if (toExclusive <= from)
            return Error.Validation("The reconciliation window must be a non-empty date range.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var propertyId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT property_id FROM channel.channel_connection WHERE id = @channelConnectionId",
            new { channelConnectionId }, cancellationToken: ct));
        if (propertyId is null)
            return Error.NotFound("connection-not-found", $"Channel connection {channelConnectionId} was not found.");

        var mappings = (await conn.QueryAsync<MappingRow>(new CommandDefinition("""
            SELECT DISTINCT external_room_code AS ExternalRoomCode, room_type_id AS RoomTypeId
            FROM channel.room_mapping WHERE channel_connection_id = @channelConnectionId
            """, new { channelConnectionId }, cancellationToken: ct))).ToList();

        var conflicts = new List<ReconciliationConflict>();
        var nightsChecked = 0;

        foreach (var mapping in mappings)
        {
            var ours = (await conn.QueryAsync<(DateOnly Date, int Available)>(new CommandDefinition("""
                SELECT stay_date AS Date, (total_allotment - units_sold - units_held) AS Available
                FROM ari.inventory_calendar
                WHERE room_type_id = @RoomTypeId AND stay_date >= @from AND stay_date < @toExclusive
                """, new { mapping.RoomTypeId, from, toExclusive }, cancellationToken: ct)))
                .ToDictionary(r => r.Date, r => r.Available);

            var theirs = await client.GetAvailabilitySnapshotAsync(channelConnectionId, mapping.ExternalRoomCode, from, toExclusive, ct);

            foreach (var night in theirs)
            {
                nightsChecked++;
                var ourAvailable = ours.GetValueOrDefault(night.Date, 0);
                if (night.Available <= ourAvailable)
                    continue; // channel offers no more than we can honor — fine

                var opened = await OpenConflictIfNewAsync(
                    conn, propertyId.Value, mapping.RoomTypeId, night.Date, ourAvailable, night.Available, ct);
                if (opened)
                    conflicts.Add(new ReconciliationConflict(mapping.RoomTypeId, night.Date, ourAvailable, night.Available, "OVERBOOK"));
            }
        }

        return Result<ReconciliationResult>.Success(new ReconciliationResult(nightsChecked, conflicts));
    }

    /// <summary>Opens an OVERBOOK conflict unless an OPEN one already exists for this room-type+date (idempotent).</summary>
    private static async Task<bool> OpenConflictIfNewAsync(
        NpgsqlConnection conn, long propertyId, long roomTypeId, DateOnly date,
        int ourAvailable, int channelAvailable, CancellationToken ct)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO channel.sync_conflict (property_id, type, detail)
            SELECT @propertyId, 'OVERBOOK',
                   jsonb_build_object('room_type_id', @roomTypeId, 'date', @date::text,
                                      'our_available', @ourAvailable, 'channel_available', @channelAvailable)
            WHERE NOT EXISTS (
                SELECT 1 FROM channel.sync_conflict
                WHERE property_id = @propertyId AND type = 'OVERBOOK' AND status = 'OPEN'
                  AND detail->>'room_type_id' = @roomTypeId::text
                  AND detail->>'date' = @date::text)
            """, new { propertyId, roomTypeId, date, ourAvailable, channelAvailable }, cancellationToken: ct));

        return affected == 1;
    }

    private sealed record MappingRow(string ExternalRoomCode, long RoomTypeId);
}
