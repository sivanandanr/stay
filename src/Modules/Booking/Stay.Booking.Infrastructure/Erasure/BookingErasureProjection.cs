using System.Text.Json;
using Dapper;
using Npgsql;
using Stay.BuildingBlocks.Outbox;
using Stay.Guest.Contracts;

namespace Stay.Booking.Infrastructure.Erasure;

/// <summary>
/// Anonymizes the booking contact snapshots when a guest is erased (BR-8). The booking carries a
/// <c>contact_email</c>/<c>contact_phone</c> copy taken at hold time; on <see cref="GuestErased"/> we
/// null/tombstone those personal fields while keeping the booking and its financial figures intact
/// (§10 — retain evidence, anonymize personal data). Cross-context by event, never by table access
/// (BR-4/§15): the guest context decides, the booking context reacts. Idempotent — the tombstone is a
/// fixed value, so an at-least-once redelivery is a harmless no-op (BR-5).
/// </summary>
public sealed class BookingErasureProjection(string connectionString)
{
    private const string Tombstone = "erased@stay.invalid";

    public static bool Handles(string eventType) => eventType == "stay.guest.erased";

    public async Task ProjectAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!Handles(envelope.Type))
            return;

        var e = JsonSerializer.Deserialize<GuestErased>(envelope.Payload)
            ?? throw new InvalidOperationException("Empty GuestErased payload.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE booking.booking
            SET contact_email = @Tombstone, contact_phone = NULL, updated_at = now()
            WHERE guest_id = @GuestId AND contact_email <> @Tombstone
            """, new { e.GuestId, Tombstone }, cancellationToken: ct));
    }
}
