using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Application.Properties.SetAmenities;
using Stay.Catalog.Contracts;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// SetPropertyAmenities against real Postgres: owner-scoped replace of the property_amenity join, an
/// idempotent full-replace, unknown-code rejection, and the PropertyAmenitiesUpdated outbox event that
/// feeds the search amenities facet.
/// </summary>
public sealed class SetPropertyAmenitiesTests : IAsyncLifetime
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

    private async Task SeedAmenitiesAsync(params string[] codes)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        foreach (var code in codes)
            await conn.ExecuteAsync("""
                INSERT INTO catalog.amenity (code, category, label) VALUES (@code, 'GENERAL', @code)
                """, new { code });
    }

    private async Task<long> CreatePropertyAsync(string ownerSub, long cityId)
    {
        await using var db = NewDbContext();
        var command = new CreatePropertyCommand(
            OwnerSub: ownerSub, Name: "Marina Bay Suites", PropertyType: "HOTEL",
            Description: null, StarRating: 5, Latitude: 1.2834, Longitude: 103.8607,
            CountryCode: "SG", CityId: cityId,
            Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
            DefaultCurrency: "SGD", Timezone: "Asia/Singapore",
            CheckInTime: null, CheckOutTime: null);
        var result = await new CreatePropertyHandler(db).Handle(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private async Task<int> CountJoinAsync(long propertyId)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM catalog.property_amenity WHERE property_id = @propertyId", new { propertyId });
    }

    [Fact]
    public async Task Owner_sets_amenities_links_them_and_emits_the_event()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        await SeedAmenitiesAsync("WIFI", "POOL", "PARKING");
        var propertyId = await CreatePropertyAsync(sub, cityId);

        await using (var db = NewDbContext())
        {
            // Codes are normalized to upper-case + de-duped on the way in.
            var result = await new SetPropertyAmenitiesHandler(db)
                .Handle(new SetPropertyAmenitiesCommand(sub, propertyId, ["wifi", "Pool", "WIFI"]), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(2, result.Value);
        }

        Assert.Equal(2, await CountJoinAsync(propertyId));
        await using (var db = NewDbContext())
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.property-amenities-updated"));
    }

    [Fact]
    public async Task Setting_amenities_again_replaces_the_whole_set()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        await SeedAmenitiesAsync("WIFI", "POOL", "PARKING");
        var propertyId = await CreatePropertyAsync(sub, cityId);

        await using (var db = NewDbContext())
            await new SetPropertyAmenitiesHandler(db)
                .Handle(new SetPropertyAmenitiesCommand(sub, propertyId, ["WIFI", "POOL"]), default);

        await using (var db = NewDbContext())
        {
            var result = await new SetPropertyAmenitiesHandler(db)
                .Handle(new SetPropertyAmenitiesCommand(sub, propertyId, ["PARKING"]), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(1, result.Value);
        }

        Assert.Equal(1, await CountJoinAsync(propertyId)); // WIFI/POOL gone, only PARKING remains
    }

    [Fact]
    public async Task An_unknown_amenity_code_is_rejected_and_nothing_is_written()
    {
        var cityId = await SeedCityAsync();
        var sub = await SeedHostAsync();
        await SeedAmenitiesAsync("WIFI");
        var propertyId = await CreatePropertyAsync(sub, cityId);

        await using var db = NewDbContext();
        var result = await new SetPropertyAmenitiesHandler(db)
            .Handle(new SetPropertyAmenitiesCommand(sub, propertyId, ["WIFI", "TELEPORTER"]), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation", result.Error!.Value.Code);
        Assert.Equal(0, await CountJoinAsync(propertyId));
        Assert.Equal(0, await db.OutboxMessages.CountAsync(m => m.Type == "stay.catalog.property-amenities-updated"));
    }

    [Fact]
    public async Task Setting_amenities_on_another_owners_property_is_not_found()
    {
        var cityId = await SeedCityAsync();
        var ownerSub = await SeedHostAsync();
        var otherSub = await SeedHostAsync();
        await SeedAmenitiesAsync("WIFI");
        var propertyId = await CreatePropertyAsync(ownerSub, cityId);

        await using var db = NewDbContext();
        var result = await new SetPropertyAmenitiesHandler(db)
            .Handle(new SetPropertyAmenitiesCommand(otherSub, propertyId, ["WIFI"]), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("property-not-found", result.Error!.Value.Code); // tenancy: don't leak existence
        Assert.Equal(0, await CountJoinAsync(propertyId));
    }
}
