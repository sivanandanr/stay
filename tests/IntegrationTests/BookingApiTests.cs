using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Stay.Ari.Infrastructure.Inventory;
using Stay.Ari.Infrastructure.Pricing;
using Stay.Booking.Contracts;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// The booking HTTP surface end to end: an authenticated guest holds inventory over <c>POST /holds</c>,
/// with first-login provisioning and Idempotency-Key behaviour, driven through the real ASP.NET host.
/// </summary>
public sealed class BookingApiTests : IAsyncLifetime
{
    private static readonly DateOnly CheckIn = new(2030, 6, 10);
    private static readonly DateOnly CheckOut = new(2030, 6, 13);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(AriSchema.Ddl);
            await conn.ExecuteAsync(BookingSchema.Ddl);
            await conn.ExecuteAsync(GuestSchema.Ddl);
            await conn.ExecuteAsync(PaymentSchema.Ddl);

            await using var tx = await conn.BeginTransactionAsync();
            await new InventoryRepository().SetAllotmentAsync(conn, tx, 7, CheckIn, CheckOut, 5);
            await new RateRepository().SetRateAsync(conn, tx, 7, 3, CheckIn, CheckOut, 100m, "SGD");
            await tx.CommitAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Stay", _postgres.GetConnectionString());
            builder.ConfigureTestServices(services =>
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { }));
        });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static HttpRequestMessage HoldRequest(string idempotencyKey, string sub = "guest|alice")
    {
        var body = new CreateHoldRequest(
            PropertyId: 99, RoomTypeId: 7, RatePlanId: 3, CheckIn: CheckIn, CheckOut: CheckOut,
            Quantity: 1, Adults: 2, Children: 0);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/holds")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Test-Sub", sub);
        return request;
    }

    [Fact]
    public async Task Post_holds_creates_a_held_booking_for_the_authenticated_guest()
    {
        var response = await _client.SendAsync(HoldRequest("key-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await response.Content.ReadFromJsonAsync<HoldResult>();
        Assert.NotNull(hold);
        Assert.Equal("HELD", hold!.Status);
        Assert.Equal(300m, hold.TotalAmount);

        // The guest was provisioned on first login.
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM guest.guest_profile WHERE identity_sub = 'guest|alice'"));
    }

    [Fact]
    public async Task Replaying_the_idempotency_key_returns_the_same_booking()
    {
        var first = await (await _client.SendAsync(HoldRequest("key-retry"))).Content.ReadFromJsonAsync<HoldResult>();
        var second = await (await _client.SendAsync(HoldRequest("key-retry"))).Content.ReadFromJsonAsync<HoldResult>();

        Assert.Equal(first!.BookingId, second!.BookingId);

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM booking.booking"));
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        var body = new CreateHoldRequest(99, 7, 3, CheckIn, CheckOut, 1, 2, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/holds") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-Sub", "guest|bob");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_is_blocked_until_the_email_is_verified()
    {
        // Hold is allowed regardless of verification.
        var hold = await (await _client.SendAsync(HoldRequest("confirm-key"))).Content.ReadFromJsonAsync<HoldResult>();

        // Confirm with an unverified email → 403 (P0-B4 policy gate).
        var blocked = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/bookings/{hold!.BookingId}/confirm");
        blocked.Headers.Add("X-Test-Sub", "guest|alice");
        blocked.Headers.Add("X-Test-Email-Verified", "false");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(blocked)).StatusCode);

        // Confirm with a verified email → 200.
        var allowed = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/bookings/{hold.BookingId}/confirm");
        allowed.Headers.Add("X-Test-Sub", "guest|alice");
        allowed.Headers.Add("X-Test-Email-Verified", "true");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(allowed)).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        // No X-Test-Sub → the test handler issues no subject → 401.
        var body = new CreateHoldRequest(99, 7, 3, CheckIn, CheckOut, 1, 2, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/holds") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", "key-x");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>Authenticates every request, taking the subject from an <c>X-Test-Sub</c> header (absent → anonymous).</summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sub = Request.Headers["X-Test-Sub"].FirstOrDefault();
        if (string.IsNullOrEmpty(sub))
            return Task.FromResult(AuthenticateResult.NoResult()); // unauthenticated → 401 on protected routes

        var emailVerified = Request.Headers["X-Test-Email-Verified"].FirstOrDefault() ?? "true";
        var identity = new ClaimsIdentity(
            [new Claim("sub", sub), new Claim("email", "guest@example.com"), new Claim("email_verified", emailVerified)],
            "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
