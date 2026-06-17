using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Catalog.Application.Properties.AddRoomType;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Application.Properties.PublishProperty;
using Stay.Catalog.Application.Properties.RejectProperty;
using Stay.Catalog.Application.Properties.SubmitForReview;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>Property moderation lifecycle (DRAFT → IN_REVIEW → LIVE / back to DRAFT) against real Postgres.</summary>
public sealed class PropertyModerationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgis/postgis:16-3.4").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync(CatalogSchema.Ddl);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private CatalogDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), o => o.UseNetTopologySuite())
            .Options;
        return new CatalogDbContext(options);
    }

    private async Task<long> SeedCityAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO catalog.city (name, country_code, geo, timezone)
            VALUES ('Singapore', 'SG', ST_SetSRID(ST_MakePoint(103.85, 1.29), 4326)::geography, 'Asia/Singapore')
            RETURNING id
            """);
    }

    private async Task<string> SeedHostAsync()
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO catalog.host (identity_sub, display_name, status)
            VALUES (@sub, 'Acme Stays', 'ACTIVE')
            """, new { sub });
        return sub;
    }

    private async Task<long> CreatePropertyAsync(string sub, long cityId)
    {
        await using var db = NewDbContext();
        var result = await new CreatePropertyHandler(db).Handle(new CreatePropertyCommand(
            OwnerSub: sub, Name: "Marina Bay Suites", PropertyType: "HOTEL", Description: null,
            StarRating: 5, Latitude: 1.2834, Longitude: 103.8607, CountryCode: "SG", CityId: cityId,
            Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
            DefaultCurrency: "SGD", Timezone: "Asia/Singapore", CheckInTime: null, CheckOutTime: null), default);
        return result.Value;
    }

    private async Task AddRoomAsync(string sub, long propertyId)
    {
        await using var db = NewDbContext();
        await new AddRoomTypeHandler(db).Handle(new AddRoomTypeCommand(
            sub, propertyId, "Deluxe", "ROOM", 10, 2, 4, null, null, null, null), default);
    }

    private async Task<PropertyStatus> StatusOfAsync(long propertyId)
    {
        await using var db = NewDbContext();
        return (await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId)).Status;
    }

    [Fact]
    public async Task Full_flow_draft_to_in_review_to_live()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(sub, cityId);

        // Cannot submit a draft with no room types.
        await using (var db = NewDbContext())
        {
            var noRooms = await new SubmitPropertyForReviewHandler(db)
                .Handle(new SubmitPropertyForReviewCommand(sub, propertyId), default);
            Assert.False(noRooms.IsSuccess);
            Assert.Equal("no-room-types", noRooms.Error!.Value.Code);
        }

        await AddRoomAsync(sub, propertyId);

        // Submit → IN_REVIEW.
        await using (var db = NewDbContext())
        {
            var submitted = await new SubmitPropertyForReviewHandler(db)
                .Handle(new SubmitPropertyForReviewCommand(sub, propertyId), default);
            Assert.True(submitted.IsSuccess, submitted.Error?.Message);
            Assert.Equal("IN_REVIEW", submitted.Value!.Status);
        }

        // Publish → LIVE (+ idempotent re-publish).
        await using (var db = NewDbContext())
        {
            var published = await new PublishPropertyHandler(db)
                .Handle(new PublishPropertyCommand("admin|1", propertyId), default);
            Assert.True(published.IsSuccess, published.Error?.Message);
            Assert.Equal("LIVE", published.Value!.Status);
        }
        await using (var db = NewDbContext())
            await new PublishPropertyHandler(db).Handle(new PublishPropertyCommand("admin|1", propertyId), default);

        Assert.Equal(PropertyStatus.Live, await StatusOfAsync(propertyId));

        await using (var db = NewDbContext())
        {
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.property-submitted"));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.property-published"));
        }
    }

    [Fact]
    public async Task Reject_sends_a_property_in_review_back_to_draft_with_a_reason()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(sub, cityId);
        await AddRoomAsync(sub, propertyId);

        await using (var db = NewDbContext())
            await new SubmitPropertyForReviewHandler(db).Handle(new SubmitPropertyForReviewCommand(sub, propertyId), default);

        await using (var db = NewDbContext())
        {
            var rejected = await new RejectPropertyHandler(db)
                .Handle(new RejectPropertyCommand("admin|1", propertyId, "Photos are too low-resolution."), default);
            Assert.True(rejected.IsSuccess, rejected.Error?.Message);
            Assert.Equal("DRAFT", rejected.Value!.Status);
        }

        Assert.Equal(PropertyStatus.Draft, await StatusOfAsync(propertyId));
        await using (var db = NewDbContext())
        {
            var outbox = await db.OutboxMessages.SingleAsync(m => m.Type == "stay.catalog.property-rejected");
            Assert.Contains("low-resolution", outbox.Payload);
        }
    }

    [Fact]
    public async Task Publishing_a_draft_is_an_invalid_transition()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(sub, cityId); // DRAFT

        await using var db = NewDbContext();
        var result = await new PublishPropertyHandler(db).Handle(new PublishPropertyCommand("admin|1", propertyId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid-state", result.Error!.Value.Code);
        Assert.Equal(ErrorType.Conflict, result.Error!.Value.Type);
    }

    [Fact]
    public async Task Another_owner_cannot_submit_a_property()
    {
        var cityId = await SeedCityAsync();
        var ownerSub = await SeedHostAsync();
        var otherSub = await SeedHostAsync();
        var propertyId = await CreatePropertyAsync(ownerSub, cityId);
        await AddRoomAsync(ownerSub, propertyId);

        await using var db = NewDbContext();
        var result = await new SubmitPropertyForReviewHandler(db)
            .Handle(new SubmitPropertyForReviewCommand(otherSub, propertyId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code); // tenancy
    }
}
