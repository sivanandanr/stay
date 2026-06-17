using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stay.BuildingBlocks;
using Stay.Catalog.Application.Properties.CreateProperty;
using Stay.Catalog.Contracts;
using Stay.Catalog.Domain.Properties;
using Stay.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stay.IntegrationTests;

/// <summary>
/// CreateProperty against real Postgres (PostGIS): proves the EF mapping (geo, jsonb address,
/// snake_case, enums), the owner-approval gate, and the outbox event written in the same transaction.
/// </summary>
public sealed class CreatePropertyTests : IAsyncLifetime
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

    private async Task<(long Id, string Sub)> SeedHostAsync(string status)
    {
        var sub = $"auth0|{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO catalog.host (identity_sub, display_name, status)
            VALUES (@sub, 'Acme Stays', @status)
            RETURNING id
            """, new { sub, status });
        return (id, sub);
    }

    private static CreatePropertyCommand ValidCommand(string ownerSub, long cityId) => new(
        OwnerSub: ownerSub,
        Name: "Marina Bay Suites",
        PropertyType: "HOTEL",
        Description: "Central, near the bay.",
        StarRating: 5,
        Latitude: 1.2834,
        Longitude: 103.8607,
        CountryCode: "SG",
        CityId: cityId,
        Address: new AddressDto("1 Bayfront Ave", null, "Singapore", null, "018971", "SG"),
        DefaultCurrency: "SGD",
        Timezone: "Asia/Singapore",
        CheckInTime: new TimeOnly(14, 0),
        CheckOutTime: new TimeOnly(11, 0));

    [Fact]
    public async Task Approved_host_creates_a_draft_property_and_emits_PropertyCreated()
    {
        var cityId = await SeedCityAsync();
        var (hostId, sub) = await SeedHostAsync("ACTIVE");

        long propertyId;
        await using (var db = NewDbContext())
        {
            var result = await new CreatePropertyHandler(db).Handle(ValidCommand(sub, cityId), default);

            Assert.True(result.IsSuccess, result.Error?.Message);
            propertyId = result.Value;
            Assert.True(propertyId > 0);
        }

        // Re-read through a fresh context to prove it really persisted with the right mapping.
        await using (var db = NewDbContext())
        {
            var property = await db.Properties.SingleAsync(p => p.Id == propertyId);

            Assert.Equal(hostId, property.HostId);
            Assert.Equal(cityId, property.CityId);
            Assert.Equal(PropertyType.Hotel, property.Type);
            Assert.Equal(PropertyStatus.Draft, property.Status);          // server sets DRAFT, not the client
            Assert.Equal("SG", property.CountryCode);
            Assert.Equal("SGD", property.DefaultCurrency);
            Assert.Equal(4326, property.Geo.SRID);
            Assert.Equal(103.8607, property.Geo.X, 4);                    // longitude
            Assert.Equal(1.2834, property.Geo.Y, 4);                     // latitude
            Assert.Equal("1 Bayfront Ave", property.Address.Line1);      // jsonb round-trip
            Assert.True(property.CreatedAt > DateTimeOffset.MinValue);

            var outbox = await db.OutboxMessages.SingleAsync();
            Assert.Equal("stay.catalog.property-created", outbox.Type);
            Assert.Null(outbox.ProcessedAt);
            Assert.Contains(propertyId.ToString(), outbox.Payload);
        }
    }

    [Fact]
    public async Task Unapproved_host_is_rejected_and_nothing_is_written()
    {
        var cityId = await SeedCityAsync();
        var (_, sub) = await SeedHostAsync("PENDING");

        await using var db = NewDbContext();
        var result = await new CreatePropertyHandler(db).Handle(ValidCommand(sub, cityId), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("owner-not-approved", result.Error!.Value.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error!.Value.Type);

        await using var verify = NewDbContext();
        Assert.Equal(0, await verify.Properties.CountAsync());
        Assert.Equal(0, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Unknown_city_is_rejected()
    {
        var (_, sub) = await SeedHostAsync("ACTIVE");

        await using var db = NewDbContext();
        var result = await new CreatePropertyHandler(db).Handle(ValidCommand(sub, cityId: 999_999), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("city-not-found", result.Error!.Value.Code);
        Assert.Equal(0, await db.Properties.CountAsync());
    }
}
