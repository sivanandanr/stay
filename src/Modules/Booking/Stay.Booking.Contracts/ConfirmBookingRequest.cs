namespace Stay.Booking.Contracts;

/// <summary>
/// Optional body for <c>POST /bookings/{id}/confirm</c>. When the guest paid via Razorpay Checkout, the
/// client returns the order/payment ids and the checkout signature; the server verifies them through
/// PaymentGateway (§9). Omitting the body uses the server-driven path (mock / pay-on-confirm).
/// </summary>
public sealed record ConfirmBookingRequest(
    string? RazorpayOrderId,
    string? RazorpayPaymentId,
    string? RazorpaySignature)
{
    /// <summary>True only when all three checkout fields are present (a complete proof to verify).</summary>
    public bool HasCheckoutProof =>
        !string.IsNullOrWhiteSpace(RazorpayOrderId)
        && !string.IsNullOrWhiteSpace(RazorpayPaymentId)
        && !string.IsNullOrWhiteSpace(RazorpaySignature);
}
