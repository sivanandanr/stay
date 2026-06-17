using Dapper;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Owner-facing setup: connect a property to a channel manager / PMS and map external room/rate
/// codes to ours. The mapping is what lets inbound ARI messages resolve to a <c>room_type_id</c>
/// (an unmapped code quarantines the message rather than guessing — see <see cref="ChannelIngestService"/>).
/// </summary>
public sealed class ChannelConnectionService(string connectionString)
{
    public async Task<Result<ChannelConnectionResponse>> ConnectAsync(ConnectChannelRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
            return Error.Validation("A channel provider is required.");
        if (string.IsNullOrWhiteSpace(request.CredentialsRef))
            return Error.Validation("A credentials reference is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO channel.channel_connection (property_id, provider, credentials_ref)
            VALUES (@PropertyId, @Provider, @CredentialsRef)
            RETURNING id
            """, request, cancellationToken: ct));

        return Result<ChannelConnectionResponse>.Success(
            new ChannelConnectionResponse(id, request.PropertyId, request.Provider, "ACTIVE"));
    }

    public async Task<Result<long>> MapRoomAsync(long channelConnectionId, MapRoomRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalRoomCode))
            return Error.Validation("An external room code is required.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM channel.channel_connection WHERE id = @channelConnectionId)",
            new { channelConnectionId }, cancellationToken: ct));
        if (!exists)
            return Error.NotFound("connection-not-found", $"Channel connection {channelConnectionId} was not found.");

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO channel.room_mapping
                (channel_connection_id, external_room_code, room_type_id, external_rate_code, rate_plan_id)
            VALUES (@channelConnectionId, @ExternalRoomCode, @RoomTypeId, @ExternalRateCode, @RatePlanId)
            ON CONFLICT (channel_connection_id, external_room_code, external_rate_code) DO UPDATE
                SET room_type_id = EXCLUDED.room_type_id, rate_plan_id = EXCLUDED.rate_plan_id
            RETURNING id
            """,
            new
            {
                channelConnectionId,
                request.ExternalRoomCode,
                request.RoomTypeId,
                request.ExternalRateCode,
                request.RatePlanId
            }, cancellationToken: ct));

        return Result<long>.Success(id);
    }
}
