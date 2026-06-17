using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.Booking.Contracts;
using Stay.BuildingBlocks.Outbox;

namespace Stay.Reviews.Infrastructure;

/// <summary>
/// Projects <see cref="BookingCompleted"/> into the <c>reviews.reviewable_stay</c> read model so the
/// guest of a completed stay can submit a verified review. Idempotent (PK on booking_id).
/// </summary>
public sealed class ReviewableStayProjection(string connectionString)
{
    public const string CompletedType = "stay.booking.completed";

    public static bool Handles(string eventType) => eventType == CompletedType;

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var completed = JsonSerializer.Deserialize<BookingCompleted>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty BookingCompleted payload.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reviews.reviewable_stay (booking_id, guest_id, property_id)
            VALUES (@BookingId, @GuestId, @PropertyId)
            ON CONFLICT (booking_id) DO NOTHING
            """, new { completed.BookingId, completed.GuestId, completed.PropertyId }, cancellationToken: ct));
    }
}
