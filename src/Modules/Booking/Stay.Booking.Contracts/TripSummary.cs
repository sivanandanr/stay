namespace Stay.Booking.Contracts;

/// <summary>A guest's booking as shown in their "trips" list.</summary>
public sealed record TripSummary(
    long BookingId,
    string Reference,
    string Status,
    long PropertyId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    decimal TotalAmount,
    string Currency,
    DateTime CreatedAt); // UTC; Dapper reads timestamptz as DateTime
