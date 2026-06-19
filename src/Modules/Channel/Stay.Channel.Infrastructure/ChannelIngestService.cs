using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.BuildingBlocks;
using Stay.Channel.Contracts;

namespace Stay.Channel.Infrastructure;

/// <summary>
/// Inbound ARI ingest — the sync-integrity core (Phase 7, Gate G5). Each message carries the
/// channel's monotonic <c>MessageSeq</c>; the platform applies strictly increasing sequences and
/// drops anything at-or-below the last applied one, so out-of-order delivery and replays are both
/// safe (ordered + idempotent). Everything for one message — the ARI calendar writes, the
/// <c>ari_sync_log</c> entry, the sequence advance via that log, the outbox event — commits in a
/// single transaction; the connection row is locked <c>FOR UPDATE</c> so concurrent messages for the
/// same connection serialize. Unmapped external codes quarantine the whole message (nothing applied)
/// rather than guessing a room.
/// </summary>
public sealed class ChannelIngestService(string connectionString)
{
    static ChannelIngestService() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    private readonly InventoryRepository _inventory = new();
    private readonly RateRepository _rates = new();

    public async Task<Result<IngestResult>> IngestAsync(
        long channelConnectionId, AriIngestMessage message, CancellationToken ct = default)
    {
        if (message.Updates.Count == 0)
            return Error.Validation("An ARI message must carry at least one update.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the connection so concurrent messages for it serialize (ordering is per-connection).
        var propertyId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT property_id FROM channel.channel_connection WHERE id = @channelConnectionId FOR UPDATE",
            new { channelConnectionId }, tx, cancellationToken: ct));
        if (propertyId is null)
            return Error.NotFound("connection-not-found", $"Channel connection {channelConnectionId} was not found.");

        var payloadHash = Hash(message);

        var lastSeq = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(MAX(message_seq), 0) FROM channel.ari_sync_log
            WHERE channel_connection_id = @channelConnectionId AND direction = 'INBOUND' AND status = 'APPLIED'
            """, new { channelConnectionId }, tx, cancellationToken: ct));

        // Ordered + replay-safe: at-or-below the last applied sequence is stale; ignore idempotently.
        if (message.MessageSeq <= lastSeq)
        {
            await LogAsync(conn, tx, channelConnectionId, message.MessageSeq, payloadHash,
                "DROPPED_STALE", $"seq {message.MessageSeq} <= last applied {lastSeq}", ct);
            await tx.CommitAsync(ct);
            return Result<IngestResult>.Success(new IngestResult(IngestOutcome.DroppedStale, lastSeq,
                $"Stale or replayed message (seq {message.MessageSeq} <= {lastSeq})."));
        }

        // Resolve every external code to a mapping BEFORE applying — an unmapped code quarantines the
        // whole message so nothing is partially applied.
        var resolved = new List<(AriUpdate Update, long RoomTypeId, long? RatePlanId)>(message.Updates.Count);
        foreach (var update in message.Updates)
        {
            var mapping = await conn.QuerySingleOrDefaultAsync<RoomMappingRow>(new CommandDefinition("""
                SELECT room_type_id AS RoomTypeId, rate_plan_id AS RatePlanId
                FROM channel.room_mapping
                WHERE channel_connection_id = @channelConnectionId
                  AND external_room_code = @ExternalRoomCode
                  AND external_rate_code IS NOT DISTINCT FROM @ExternalRateCode
                ORDER BY id
                LIMIT 1
                """, new { channelConnectionId, update.ExternalRoomCode, update.ExternalRateCode },
                tx, cancellationToken: ct));

            if (mapping is null)
            {
                var detail = $"No room mapping for external_room_code '{update.ExternalRoomCode}'"
                    + (update.ExternalRateCode is null ? "" : $" / rate '{update.ExternalRateCode}'") + ".";
                await LogAsync(conn, tx, channelConnectionId, message.MessageSeq, payloadHash, "QUARANTINED", detail, ct);
                await tx.CommitAsync(ct);
                return Result<IngestResult>.Success(new IngestResult(IngestOutcome.Quarantined, lastSeq, detail));
            }

            resolved.Add((update, mapping.RoomTypeId, mapping.RatePlanId));
        }

        foreach (var (update, roomTypeId, ratePlanId) in resolved)
        {
            if (update.Allotment is { } allotment)
                await _inventory.SetAllotmentAsync(conn, tx, roomTypeId, update.From, update.ToExclusive, allotment, ct);

            if (update.BasePrice is { } basePrice && ratePlanId is { } planId && !string.IsNullOrWhiteSpace(update.Currency))
                await _rates.SetRateAsync(conn, tx, roomTypeId, planId, update.From, update.ToExclusive, basePrice, update.Currency!, null, ct);
        }

        await LogAsync(conn, tx, channelConnectionId, message.MessageSeq, payloadHash,
            "APPLIED", $"{resolved.Count} update(s) applied", ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE channel.channel_connection SET last_sync_at = now() WHERE id = @channelConnectionId",
            new { channelConnectionId }, tx, cancellationToken: ct));

        var @event = new ChannelAriApplied(Guid.NewGuid(), channelConnectionId, propertyId.Value, message.MessageSeq, DateTimeOffset.UtcNow);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO channel.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
            new { type = @event.EventType, payload = JsonSerializer.Serialize(@event) }, tx, cancellationToken: ct));

        // Surface a "from" price to the search read model: the lowest base rate this message carried.
        var pricedUpdates = resolved
            .Where(r => r.Update.BasePrice is not null && !string.IsNullOrWhiteSpace(r.Update.Currency))
            .ToList();
        if (pricedUpdates.Count > 0)
        {
            var fromPrice = pricedUpdates.Min(r => r.Update.BasePrice!.Value);
            var currency = pricedUpdates[0].Update.Currency!;
            var priceEvent = new PropertyPriceChanged(
                Guid.NewGuid(), propertyId.Value, fromPrice, currency, DateTimeOffset.UtcNow);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO channel.outbox_message (type, payload) VALUES (@type, CAST(@payload AS jsonb))",
                new { type = priceEvent.EventType, payload = JsonSerializer.Serialize(priceEvent) }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return Result<IngestResult>.Success(new IngestResult(IngestOutcome.Applied, message.MessageSeq, null));
    }

    private static Task LogAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long channelConnectionId,
        long messageSeq, string payloadHash, string status, string detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO channel.ari_sync_log
                (channel_connection_id, direction, message_seq, payload_hash, status, detail)
            VALUES (@channelConnectionId, 'INBOUND', @messageSeq, @payloadHash, @status, @detail)
            """, new { channelConnectionId, messageSeq, payloadHash, status, detail }, tx, cancellationToken: ct));

    private static string Hash(AriIngestMessage message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));
        return Convert.ToHexStringLower(bytes);
    }

    private sealed record RoomMappingRow(long RoomTypeId, long? RatePlanId);
}
