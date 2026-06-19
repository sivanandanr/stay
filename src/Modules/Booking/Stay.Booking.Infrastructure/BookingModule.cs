using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.Ari.Infrastructure.Availability;
using Stay.Booking.Infrastructure.Rooms;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Http;
using Stay.Booking.Contracts;
using Stay.Booking.Infrastructure.Holds;
using Stay.Booking.Infrastructure.Reminders;
using Stay.Booking.Infrastructure.Reporting;
using Stay.Booking.Infrastructure.Trips;
using Stay.Guest.Contracts;
using Stay.Loyalty.Infrastructure;
using Stay.Payment.Contracts;
using Stay.Promotion.Infrastructure;

namespace Stay.Booking.Infrastructure;

public sealed class BookingModule : IModule
{
    private static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(15);

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Stay")
            ?? throw new InvalidOperationException("Missing connection string 'Stay'.");

        services.AddSingleton(sp => new BookingHoldService(
            connectionString, sp.GetRequiredService<PromotionService>(), sp.GetRequiredService<LoyaltyService>()));
        services.AddSingleton(sp => new BookingConfirmService(
            connectionString, sp.GetRequiredService<IPaymentGateway>(),
            sp.GetRequiredService<PromotionService>(), sp.GetRequiredService<LoyaltyService>()));
        services.AddSingleton(sp => new CancelBookingService(connectionString, sp.GetRequiredService<IPaymentGateway>()));
        services.AddSingleton(new ModifyBookingService(connectionString));
        services.AddSingleton(new HoldReaper(connectionString));
        services.AddHostedService<HoldReaperService>();
        services.AddSingleton(new ReminderScheduler(connectionString));
        services.AddHostedService<ReminderSchedulerService>();
        services.AddSingleton(new StayCompletionService(connectionString));
        services.AddHostedService<StayCompletionServiceHost>();
        services.AddSingleton(new TripsQueryService(connectionString));
        services.AddSingleton(new ManualOverrideService(connectionString));
        services.AddSingleton(new ReportingService(connectionString));
        services.AddSingleton(new AvailabilityService(connectionString));
        services.AddSingleton(new PropertyRoomsQueryService(connectionString));
        services.AddSingleton(new Erasure.BookingErasureProjection(connectionString));
        services.AddHostedService<Erasure.BookingErasureConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/booking/ping", () => Results.Ok(new { module = "Booking", ok = true }));
        MapPropertyRooms(endpoints);
        MapAvailability(endpoints);
        MapCreateHold(endpoints);
        MapPaymentOrder(endpoints);
        MapConfirm(endpoints);
        MapModify(endpoints);
        MapCancel(endpoints);
        MapMyTrips(endpoints);
        MapOverride(endpoints);
        MapReports(endpoints);
    }

    // Ops/finance booking + revenue summary for a date window.
    private static void MapReports(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/admin/reports/bookings", async (
            DateTimeOffset from, DateTimeOffset to, ReportingService reporting, CancellationToken ct) =>
        {
            if (to <= from)
                return ResultHttpExtensions.Problem(Error.Validation("The report window must be a non-empty range."));
            return Results.Ok(await reporting.BookingSummaryAsync(from, to, ct));
        })
        .RequireAuthorization("ops")
        .WithName("BookingReport");

    // Ops manually overrides a booking's status (force-cancel / no-show / complete) with a mandatory
    // reason — audited (§10). Server-side authorization from admin.role_assignment via the "ops" policy.
    private static void MapOverride(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/admin/bookings/{bookingId:long}/override", async (
            long bookingId,
            ManualOverrideRequest request,
            ClaimsPrincipal user,
            ManualOverrideService overrides,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            var result = await overrides.AdjustStatusAsync(bookingId, sub, request.ToStatus, request.Reason, ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization("ops")
        .WithName("OverrideBooking");

    // The authenticated guest's own bookings ("my trips").
    private static void MapMyTrips(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/me/trips", async (
            int? page, int? pageSize,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            TripsQueryService trips,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            var items = await trips.GetTripsAsync(
                guest.GuestId, Math.Max(0, (page ?? 1) - 1), Math.Clamp(pageSize ?? 20, 1, 100), ct);
            return Results.Ok(new { items });
        })
        .RequireAuthorization()
        .WithName("GetMyTrips");

    // Guest changes the stay dates of their own confirmed booking.
    private static void MapModify(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/bookings/{bookingId:long}/modify", async (
            long bookingId,
            ModifyBookingRequest request,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            ModifyBookingService saga,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            var result = await saga.ModifyAsync(bookingId, request.CheckIn, request.CheckOut, requireGuestId: guest.GuestId, ct: ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("ModifyBooking");

    // The funnel's "choose a room" step: a property's bookable room types + rate plans. Anonymous (§6).
    private static void MapPropertyRooms(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/properties/{propertyId:long}/rooms", async (
                long propertyId, PropertyRoomsQueryService rooms, CancellationToken ct) =>
            Results.Ok(await rooms.GetRoomsAsync(propertyId, ct)))
            .WithName("PropertyRooms"); // anonymous browse

    // Read-only rooms-and-rates preview for the funnel: price + availability for a date range, without
    // holding inventory. Anonymous — browsing is open (§6); only the hold/confirm require a session.
    private static void MapAvailability(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/availability/quote", async (
            long roomTypeId, long ratePlanId, DateOnly checkIn, DateOnly checkOut,
            short? adults, short? children, int? quantity,
            AvailabilityService availability, CancellationToken ct) =>
        {
            if (checkOut <= checkIn)
                return ResultHttpExtensions.Problem(Error.Validation("Check-out must be after check-in."));
            var qty = quantity ?? 1;
            if (qty <= 0)
                return ResultHttpExtensions.Problem(Error.Validation("Quantity must be positive."));

            var occupancy = (adults ?? 2) + (children ?? 0);
            var quote = await availability.QuoteAsync(roomTypeId, ratePlanId, checkIn, checkOut, occupancy, qty, ct);
            return Results.Ok(quote);
        })
        .WithName("AvailabilityQuote"); // anonymous browse

    // Guest holds inventory. The guest is provisioned from the token; idempotency from the header (§5).
    private static void MapCreateHold(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/holds", async (
            CreateHoldRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            BookingHoldService saga,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ResultHttpExtensions.Problem(Error.Validation("The Idempotency-Key header is required."));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);

            var result = await saga.HoldAsync(new HoldRequest(
                IdempotencyKey: idempotencyKey,
                GuestId: guest.GuestId,
                ContactEmail: guest.Email ?? "",
                PropertyId: request.PropertyId,
                RoomTypeId: request.RoomTypeId,
                RatePlanId: request.RatePlanId,
                CheckIn: request.CheckIn,
                CheckOut: request.CheckOut,
                Quantity: request.Quantity,
                Adults: request.Adults,
                Children: request.Children,
                HoldTtl: HoldTtl,
                Cancellation: request.Cancellation,
                CouponCode: request.CouponCode,
                RedeemPoints: request.RedeemPoints), ct);

            return result.ToHttp(hold => Results.Created($"/api/v1/bookings/{hold.BookingId}", hold));
        })
        .RequireAuthorization()
        .WithName("CreateHold");

    // SCAFFOLD (§9, unverified): open a Razorpay Checkout order for the guest's HELD booking. The mobile
    // client takes the returned order id + public key into the Razorpay SDK, completes payment, then calls
    // confirm. Idempotency-Key required (BR-5). Tenancy-scoped by guest id.
    private static void MapPaymentOrder(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/bookings/{bookingId:long}/payment-order", async (
            long bookingId,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            BookingConfirmService saga,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ResultHttpExtensions.Problem(Error.Validation("The Idempotency-Key header is required."));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            var result = await saga.CreatePaymentOrderAsync(bookingId, requireGuestId: guest.GuestId, ct: ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("CreatePaymentOrder");

    // Confirm a held booking (mock payment for now; real confirm is payment-callback-driven).
    // Browse + hold are allowed unverified; confirming requires a verified email behind a policy flag (P0-B4).
    private static void MapConfirm(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/bookings/{bookingId:long}/confirm", async (
            long bookingId, ConfirmBookingRequest? request, ClaimsPrincipal user, IConfiguration config,
            BookingConfirmService saga, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(user.Subject()))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            if (config.GetValue("Booking:RequireEmailVerifiedToConfirm", true) && !user.EmailVerified())
                return ResultHttpExtensions.Problem(new Error(
                    "email-not-verified", "Verify your email before confirming a booking.", ErrorType.Forbidden));

            // Client-driven Razorpay path: a complete checkout proof is verified server-side (§9).
            var proof = request is { HasCheckoutProof: true }
                ? new CheckoutProof(request.RazorpayOrderId!, request.RazorpayPaymentId!, request.RazorpaySignature!)
                : null;

            var result = await saga.ConfirmAsync(bookingId, proof: proof, ct: ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("ConfirmBooking");

    // Guest cancels their own booking (tenancy-scoped by guest id).
    private static void MapCancel(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/bookings/{bookingId:long}/cancel", async (
            long bookingId,
            CancelBookingRequest request,
            ClaimsPrincipal user,
            IGuestProvisioning guests,
            CancelBookingService saga,
            CancellationToken ct) =>
        {
            var sub = user.Subject();
            if (string.IsNullOrWhiteSpace(sub))
                return ResultHttpExtensions.Problem(new Error("unauthenticated", "Token has no subject claim.", ErrorType.Unauthorized));

            var guest = await guests.ProvisionAsync(sub, user.Email(), user.Name(), user.EmailVerified(), ct);
            var result = await saga.CancelAsync(bookingId, request.Reason, "GUEST", requireGuestId: guest.GuestId, ct: ct);
            return result.ToHttp(Results.Ok);
        })
        .RequireAuthorization()
        .WithName("CancelBooking");
}
