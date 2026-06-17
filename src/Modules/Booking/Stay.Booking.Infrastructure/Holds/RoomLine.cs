namespace Stay.Booking.Infrastructure.Holds;

/// <summary>The inventory-affecting fields of a booking_room, read during confirm/reap.</summary>
internal sealed record RoomLine(long RoomTypeId, DateOnly CheckIn, DateOnly CheckOut, int Quantity);
